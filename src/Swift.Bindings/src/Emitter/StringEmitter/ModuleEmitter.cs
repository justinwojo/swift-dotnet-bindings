// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
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

        /// <summary>
        /// Initializes a new instance of the <see cref="StringEmitter"/> class.
        /// </summary>
        public StringEmitter(
            string outputDirectory,
            ITypeDatabase typeDatabase,
            ILoggerFactory loggerFactory,
            NamespacePatternResolver? namespacePatternResolver = null)
        {
            _outputDirectory = outputDirectory;
            _typeDatabase = typeDatabase;
            _logger = loggerFactory.CreateLogger<StringEmitter>();
            _namespacePatternResolver = namespacePatternResolver ?? new NamespacePatternResolver();
            _conductor = new Conductor(loggerFactory, _namespacePatternResolver);
        }

        /// <summary>
        /// Emits a C# module based on the module declaration.
        /// </summary>
        /// <param name="moduleDecl">The module declaration.</param>
        public void EmitModule(ModuleDecl moduleDecl)
        {
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
                    moduleHandler.Emit(csWriter, swiftWriter, env, _conductor);
                    collectedViews = SwiftUIBridgeCollector.GetCollectedViews();
                }
                finally
                {
                    SwiftUIBridgeCollector.Reset();
                }

                string csOutputPath = Path.Combine(_outputDirectory, $"{@namespace}.cs");
                using (StreamWriter outputFile = new(csOutputPath))
                {
                    outputFile.Write(csStringWriter.ToString());
                }
                string swiftOutputPath = Path.Combine(_outputDirectory, $"{@namespace}.swift");
                using (StreamWriter outputFile = new(swiftOutputPath))
                {
                    outputFile.Write(swiftStringWriter.ToString());
                }

                if (collectedViews.Count > 0)
                {
                    SwiftUIBridgeEmitter.EmitBridgeFiles(
                        _outputDirectory, @namespace, moduleDecl.Name, collectedViews, _logger, _typeDatabase, moduleDecl);
                }
            }
            else
            {
                _logger.LogWarning($"No module handler found for {moduleDecl.Name}");
            }
        }
    }
}
