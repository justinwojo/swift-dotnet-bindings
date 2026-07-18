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
                    // Pre-pass: populate the per-protocol "actually-emitted interface property names"
                    // cache for every protocol in the module BEFORE any interface body is emitted.
                    // Downstream consumers (proxy explicit-impl forwarders, BFS shadow detection,
                    // covariant-return forwarders) read this set to compute method projection keys
                    // with the same collision context the interface itself used. Lazy population
                    // during emission was order-dependent; this prepass makes the cache
                    // declaration-order-independent. See InterfacePropertyNamePrecomputer.
                    InterfacePropertyNamePrecomputer.Precompute(moduleDecl, _typeDatabase, emissionContext);
                    // Pre-pass: register every concrete error-conforming type (Swift.Error /
                    // Foundation.LocalizedError) from this module on the emission context with a
                    // stable id for the plain-throws → SwiftException<TError> bridge — the
                    // in-memory registry is consumed by the wire-format extension and Swift
                    // cascade helper that follow. See ErrorEnumRegistryEmitter.
                    ErrorEnumRegistryEmitter.Precompute(moduleDecl, emissionContext);
                    var initialContext = new TypeHandlerContext(null, new(), null, MarkerProtocolConformances: _markerProtocolConformances, EmissionContext: emissionContext);
                    moduleHandler.Emit(csWriter, swiftWriter, env, _conductor, initialContext);
                    collectedViews = SwiftUIBridgeCollector.GetCollectedViews(emissionContext);
                }

                var preQualifyOutput = csStringWriter.ToString();

                // CX-1: When the namespace has a type with the same name (e.g., a module named "Foo"
                // has a class also named "Foo"), C# resolves Foo.OtherType as a nested type lookup on the
                // class instead of a namespace member. Fix by adding global:: qualifier. Hoisted out of
                // the write so the file-per-type split can apply the same qualification to each file.
                var collisionType = moduleDecl.Types.FirstOrDefault(t => t.Name == @namespace)
                    ?? (BaseDecl?)moduleDecl.Protocols.FirstOrDefault(p => p.Name == @namespace);
                var nestedTypeNames = new HashSet<string>();
                if (collisionType is TypeDecl td)
                {
                    // Collect nested type C# names — Namespace.NestedType refers to the collision class's
                    // nested type, not a namespace member. These must NOT get global:: qualification.
                    // Use C# names (not Swift names) because nested type renames (e.g., Connection → ConnectionKind)
                    // mean the generated code uses the renamed name.
                    foreach (var nested in td.Types)
                    {
                        var csLeafName = NameProvider.ToPascalCaseForTypeName(nested.Name);
                        // Check TypeDatabase for rename (e.g., Connection → ConnectionKind)
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

                // QualifyNamespaceReferences is position-independent (local lookahead only), so
                // running it per output file is equivalent to running it once on the combined
                // string and slicing. No-op unless the module name collides with a type name.
                string Qualify(string source) =>
                    collisionType != null ? QualifyNamespaceReferences(source, @namespace, nestedTypeNames) : source;

                var wholeOutput = Qualify(preQualifyOutput);

                // Post-generation ABI contract validation — over the whole module, exactly as before.
                AbiContractChecker.Validate(wholeOutput, moduleDecl.Name, _logger);

                // Split the combined output into one file per top-level type (prelude keeps the
                // historical {namespace}.cs name). Byte-for-byte a repackaging of wholeOutput —
                // zero public-API change; the union of all files is exactly wholeOutput.
                WriteModuleFiles(preQualifyOutput, wholeOutput, @namespace, emissionContext, Qualify);

                // Emit the API manifest ({namespace}.api-manifest.json) alongside the .cs.
                // It records every emitted public member's post-collision C# signature → native
                // entry symbol so the ratchet gate can detect a same-signature symbol retarget.
                ApiManifestEmitter.Emit(moduleDecl.Name, @namespace, emissionContext, _outputDirectory, _logger);

                // Emit the human-readable member table ({namespace}.api-surface.md) from the SAME
                // emitted-surface facts, so a consumer-facing README can derive its member list from
                // what the generator actually emitted instead of a hand-authored (drift-prone) list.
                ApiSurfaceDocEmitter.Emit(moduleDecl.Name, @namespace, emissionContext, _outputDirectory, _logger);

                string swiftOutputPath = Path.Combine(_outputDirectory, $"{@namespace}.Wrapper.swift");
                // Module/type-name collision rewrite (formerly Pattern 5 in SwiftWrapperPostProcessor):
                // when the module has a public type with the same name as the module, bare
                // "Module.X" references resolve to the type's nested member, not the module-level
                // X. Apply the collision-aware rewrite once at the wrapper file boundary, driven
                // by the structurally-aware ModuleEmissionContext (knows which types are nested in
                // the colliding class so e.g. LoggingLib.Level stays qualified).
                var swiftOutput = emissionContext.QualifyForWrapperSource(swiftStringWriter.ToString());
                // Trap-anonymity lint: verify every emitted fatalError/preconditionFailure carries the
                // [SwiftBindings] breadcrumb and report the force-cast surface. Read-only.
                EmittedSwiftTrapLint.Validate(swiftOutput, $"{@namespace}.Wrapper.swift", _logger);
                using (StreamWriter outputFile = new(swiftOutputPath))
                {
                    outputFile.Write(swiftOutput);
                }

                // Write per-architecture assembly thunk files if any thunks were emitted. The
                // x86_64 set may be a strict subset of the ARM64 set (some signatures spill past
                // the SysV register files and fall back to @_cdecl wrappers on x86_64).
                WriteThunkAssemblyFile(emissionContext.HasThunkAssembly, ThunkTargetArch.Arm64,
                    emissionContext.AssemblyBuilder, moduleDecl.Name, @namespace);
                WriteThunkAssemblyFile(emissionContext.HasX64ThunkAssembly, ThunkTargetArch.X86_64,
                    emissionContext.X64AssemblyBuilder, moduleDecl.Name, @namespace);

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
                        _outputDirectory, @namespace, moduleDecl.Name, collectedViews, _logger, _typeDatabase, moduleDecl, _bridgeHintsPath, emissionContext);
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
                        viewBridgeExists: hasViews, _logger, emissionContext);
                }
            }
            else
            {
                _logger.LogWarning($"No module handler found for {moduleDecl.Name}");
            }
        }

        /// <summary>
        /// Writes a per-architecture native thunk assembly file (<c>{namespace}.{arch}.s</c>) when
        /// any thunks were emitted for that architecture.
        /// </summary>
        private void WriteThunkAssemblyFile(bool hasThunks, ThunkTargetArch target,
            System.Text.StringBuilder assembly, string moduleName, string @namespace)
        {
            if (!hasThunks)
                return;

            var asmContent = new System.Text.StringBuilder();
            target.EmitFileHeader(asmContent, moduleName);
            asmContent.Append(assembly);
            asmContent.Append(target.EmitFileFooter());

            string asmOutputPath = Path.Combine(_outputDirectory, $"{@namespace}.{target.ArchTag}.s");
            using StreamWriter outputFile = new(asmOutputPath);
            outputFile.Write(asmContent.ToString());
        }

        /// <summary>
        /// Replaces bare namespace-qualified type references with global:: qualified references.
        /// Called when the module namespace collides with a type name (e.g., a module named "Foo" has class "Foo").
        /// </summary>
        /// <param name="csOutput">The generated C# source code.</param>
        /// <param name="namespace">The namespace that collides with a type name.</param>
        /// <param name="nestedTypeNames">Names of types nested within the collision class.
        /// References like Namespace.NestedType should NOT be qualified because they resolve
        /// to nested types of the class, not namespace members.</param>
        internal static string QualifyNamespaceReferences(string csOutput, string @namespace, HashSet<string> nestedTypeNames)
        {
            // Match Namespace.Identifier where Namespace is NOT preceded by global:: or another identifier char,
            // and NOT in a namespace declaration (e.g., namespace Foo.SwiftInterop).
            // This handles type references like Foo.SomeType in parameter types, return types,
            // generic arguments, typeof(), casts, etc.
            var escapedNs = Regex.Escape(@namespace);
            // The lookahead admits an underscore start as well as an uppercase one: projected
            // companion types keep a leading-underscore prefix (e.g. an ObjC-projected peer),
            // and those references need the same qualification as PascalCase ones. It stays
            // deliberately narrow — a lowercase start would also match a genuine static-member
            // access on the colliding class (Namespace.someMember), which must NOT be rewritten.
            var pattern = $@"(?<!global::)(?<!namespace )(?<![.\w]){escapedNs}\.(?=[A-Z_])";
            return Regex.Replace(csOutput, pattern, match =>
            {
                // Check if the following identifier is a nested type of the collision class.
                // If so, leave it unqualified — Foo.NestedType refers to the nested type, not a namespace member.
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

        /// <summary>
        /// Writes the module output as one file per top-level type. The prelude keeps the
        /// historical <c>{namespace}.cs</c> name and holds the shared header, free functions,
        /// module-level trailers, namespace close, and the <c>SwiftInterop</c> companion;
        /// each top-level type goes to <c>{namespace}.Types.{Leaf}.cs</c> with the same header
        /// and namespace-closing brace replayed around its own byte-range. The union of the
        /// distinct content across all files is exactly <paramref name="wholeOutput"/>, so the
        /// split is a pure repackaging with zero public-API change. Falls back to a single
        /// combined file when the emitter recorded no usable span data.
        /// </summary>
        private void WriteModuleFiles(
            string preQualifyOutput, string wholeOutput, string @namespace,
            ModuleEmissionContext emissionContext, Func<string, string> qualify)
        {
            var preludePath = Path.Combine(_outputDirectory, $"{@namespace}.cs");

            // Remove stale per-type files from a prior regen so a since-removed type does not
            // linger and get compiled by the {namespace}.Types.*.cs Compile glob.
            if (Directory.Exists(_outputDirectory))
            {
                foreach (var stale in Directory.EnumerateFiles(
                             _outputDirectory, SplitFileNaming.TypeFileGlob(@namespace)))
                {
                    File.Delete(stale);
                }
            }

            var files = ModuleFileSplitter.BuildFileSet(
                preQualifyOutput, @namespace,
                emissionContext.EmissionNamespaceBodyStart,
                emissionContext.EmissionNamespaceBodyEnd,
                emissionContext.EmissionNamespaceCloseEnd,
                emissionContext.TopLevelTypeSpans,
                qualify);

            if (files == null)
            {
                // No usable split data (e.g. a module with only free functions) — write the
                // single combined file, identical to the pre-split behavior.
                File.WriteAllText(preludePath, wholeOutput);
                return;
            }

            foreach (var file in files)
                File.WriteAllText(Path.Combine(_outputDirectory, file.FileName), file.Content);
        }
    }
}
