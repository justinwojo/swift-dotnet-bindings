using System.Globalization;
using Microsoft.Extensions.Logging;
using TbdParsing;

namespace BindingsGeneration.Demangling;

/// <summary>
/// A class to contain results from demangling the set of symbols in TBD file.
/// </summary>
public class DemanglingResults
{
    /// <summary>
    /// Constructs a DemanglingResults object with the desired reductions separated out
    /// </summary>
    /// <param name="reductions">An array of reductions</param>
    DemanglingResults(IReduction[] reductions, HashSet<string>? allSymbols = null)
    {
        Errors = ArrayOf<ReductionError>(reductions);
        MetadataAccessors = ArrayOf<MetadataAccessorReduction>(reductions);
        DispatchThunks = ArrayOf<DispatchThunkFunctionReduction>(reductions);
        ProtocolWitnessTables = ArrayOf<ProtocolWitnessTableReduction>(reductions);
        ProtocolConformanceDescriptors = ArrayOf<ProtocolConformanceDescriptorReduction>(reductions);
        AllSymbols = allSymbols ?? new HashSet<string>();
    }

    /// <summary>
    /// A utility routine to filter out a specific type of reduction from a set of general reductions
    /// </summary>
    /// <typeparam name="T">The type of the desired aggregation of reductions</typeparam>
    /// <param name="reductions">an array of general reductions to filter</param>
    /// <returns>An array of the requested filtered reductions</returns>
    static T[] ArrayOf<T>(IReduction[] reductions) where T : IReduction
    {
        return reductions.OfType<T>().ToArray();
    }

    /// <summary>
    /// All errors encountered while demangling symbols
    /// </summary>
    public ReductionError[] Errors { get; private set; }

    /// <summary>
    /// All type metadata accessor functions encountered while demangling symbols
    /// </summary>
    public MetadataAccessorReduction[] MetadataAccessors { get; private set; }

    /// <summary>
    /// All dispatch thunk functions encountered while demangling symbols
    /// </summary>
    public DispatchThunkFunctionReduction[] DispatchThunks { get; private set; }

    /// <summary>
    /// All protocol witness tables found while demangling symbols
    /// </summary>
    public ProtocolWitnessTableReduction[] ProtocolWitnessTables { get; private set; }

    /// <summary>
    /// All protocol conformance descriptors found while demangling symbols
    /// </summary>
    public ProtocolConformanceDescriptorReduction[] ProtocolConformanceDescriptors { get; private set; }

    /// <summary>
    /// All raw symbols from the TBD file (with leading underscore stripped).
    /// Used for detecting async property accessors via the "Tu" suffix convention.
    /// </summary>
    public HashSet<string> AllSymbols { get; private set; }

    /// <summary>
    /// Factory method to generate a suite of demangling results from the given TBD file.
    /// </summary>
    /// <param name="path">Path to the TBD file.</param>
    /// <param name="loggerFactory">ILoggerFactory instance.</param>
    /// <returns>A set of demangling results.</returns>
    public static DemanglingResults FromTbd(string path, ILoggerFactory loggerFactory)
    {
        var tbdParser = new TbdParser(loggerFactory);
        var tbdFile = tbdParser.ParseFile(path);
        var logger = loggerFactory.CreateLogger<DemanglingResults>();

        var demangler = new Swift5Demangler();

        // Run demangler for each export and aggregate results
        // Catch exceptions for individual symbols so that a single bad symbol doesn't crash the whole process
        // Collect all symbol names (with leading underscore stripped) for raw lookup
        var allSymbols = new HashSet<string>(
            tbdFile.Exports.SelectMany(export => export.SwiftSymbols.Select(sym =>
                sym.Name.StartsWith('_') ? sym.Name[1..] : sym.Name)));

        var allReductions = tbdFile.Exports.SelectMany(export => export.SwiftSymbols.Select(sym =>
        {
            var symbolName = sym.Name.StartsWith('_') ? sym.Name[1..] : sym.Name;
            try
            {
                return demangler.Run(symbolName);
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Failed to demangle symbol '{symbolName}': {ex.Message}");
                return new ReductionError { Symbol = symbolName, Message = ex.Message };
            }
        })).ToArray();

        WarnIfNoSymbolsForOwnLibrary(logger, path, tbdFile, allSymbols);

        return new DemanglingResults(allReductions, allSymbols);
    }

    /// <summary>
    /// Tripwire for a symbol set that cannot belong to the library the `.tbd` describes.
    ///
    /// The stable Swift mangling encodes the defining module as a length-prefixed identifier right
    /// after the `$s` prefix, and a framework's own `.tbd` names that framework in its install name.
    /// So if the file yields Swift symbols but none of them are mangled for the library the file is
    /// named for, the parse dropped the library's own exports — which is silent everywhere else,
    /// because the symbol set is only ever consulted with `Contains` (async-accessor and protocol
    /// method-descriptor probes), and a missing symbol reads as a legitimate "not async" / "no
    /// descriptor" answer. This is a warning rather than a failure: it reports what the parse saw
    /// and lets generation continue.
    /// </summary>
    private static void WarnIfNoSymbolsForOwnLibrary(
        ILogger logger, string path, TbdParsing.Models.TbdFile tbdFile, HashSet<string> allSymbols)
    {
        // No Swift symbols at all is normal (a pure Objective-C framework) and says nothing about
        // which library they came from.
        if (allSymbols.Count == 0)
            return;

        string? expectedModule = ModuleNameFromInstallName(tbdFile.InstallName);
        if (expectedModule is null)
            return;

        var modulesSeen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var symbol in allSymbols)
        {
            if (ManglingProbes.TryGetModuleFromMangledName(symbol, out var module))
                modulesSeen.Add(module);
        }

        if (modulesSeen.Count == 0 || modulesSeen.Contains(expectedModule))
            return;

        // A framework whose whole Swift surface is extensions on types it does not own (Foundation
        // overlays, for instance) legitimately exports nothing mangled with itself as the LEADING
        // module — the leading module is the extended type's. Its own module still appears in the
        // symbol, as the length-prefixed extension context (`$s10Foundation…15LinkPresentationE…`),
        // so treat that as evidence the library's own exports were read and stay quiet. A parse that
        // actually dropped the library's document leaves the name absent everywhere.
        string extensionContextToken = expectedModule.Length.ToString(CultureInfo.InvariantCulture) + expectedModule;
        foreach (var symbol in allSymbols)
        {
            if (symbol.Contains(extensionContextToken, StringComparison.Ordinal))
                return;
        }

        logger.LogWarning(
            "No Swift symbol in '{Path}' is mangled for module '{ExpectedModule}' (derived from install-name " +
            "'{InstallName}'). Parsed {DocumentCount} document(s) with install-names [{InstallNames}]; the " +
            "{SymbolCount} Swift symbols found belong to [{ModulesSeen}]. Async-accessor and protocol " +
            "method-descriptor detection read this symbol set, so members of '{ExpectedModule}' may bind as " +
            "synchronous or lose their protocol conformances.",
            path, expectedModule, tbdFile.InstallName, tbdFile.DocumentCount,
            string.Join(", ", tbdFile.InstallNames), allSymbols.Count,
            string.Join(", ", modulesSeen.Take(10)), expectedModule);
    }

    /// <summary>
    /// Best-effort Swift module name for a Mach-O install name: the last path component with a
    /// dynamic-library decoration removed (`/…/VisionKit.framework/VisionKit` → `VisionKit`,
    /// `@rpath/libFoo.dylib` → `Foo`). Returns null when there is nothing usable to compare against.
    /// </summary>
    private static string? ModuleNameFromInstallName(string installName)
    {
        if (string.IsNullOrWhiteSpace(installName))
            return null;

        string leaf = installName.AsSpan(installName.LastIndexOf('/') + 1).ToString();
        if (leaf.EndsWith(".dylib", StringComparison.Ordinal))
            leaf = leaf[..^".dylib".Length];
        if (leaf.StartsWith("lib", StringComparison.Ordinal) && leaf.Length > 3)
            leaf = leaf[3..];

        return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
    }

    /// <summary>
    /// Retrieve MetadataAccessor for a type.
    /// </summary>
    /// <param name="swiftTypeName">The Swift type name.</param>
    /// <returns>The mangled name of the metadata accessor.</returns>
    /// exception cref="Exception">
    /// Thrown if the metadata accessor is not found in demangled results.
    /// </exception>
    public string GetMetadataAccessor(SwiftTypeName swiftTypeName)
    {
        var metadataAccessor = MetadataAccessors.FirstOrDefault(x => x.TypeSpec.Name == swiftTypeName.ModuleQualifiedName);

        if (metadataAccessor == null)
        {
            throw new Exception($"Metadata accessor not found for type '{swiftTypeName}'.");
        }

        return metadataAccessor.Symbol;
    }

    /// <summary>
    /// Non-throwing variant of <see cref="GetMetadataAccessor"/>. Returns false when the
    /// TBD does not contain a metadata accessor for the requested type. Lets callers fall
    /// back to the canonical Swift mangling convention (`{mangledName}Ma`) for types whose
    /// accessor symbol lives in a different framework's TBD — e.g., umbrella re-exports
    /// where RealityFoundation re-exports RealityKit's `TextureResource.Semantic` enum.
    /// </summary>
    public bool TryGetMetadataAccessor(SwiftTypeName swiftTypeName, out string symbol)
    {
        var metadataAccessor = MetadataAccessors.FirstOrDefault(x => x.TypeSpec.Name == swiftTypeName.ModuleQualifiedName);
        if (metadataAccessor == null)
        {
            symbol = string.Empty;
            return false;
        }
        symbol = metadataAccessor.Symbol;
        return true;
    }

    /// <summary>
    /// Retrieve ProtocolConformanceDescriptor for a type.
    /// </summary>
    /// <param name="implementingType">The implementing Swift type.</param>
    /// <param name="protocol">The Swift protocol.</param>
    /// <returns>The mangled name of the protocol conformance descriptor.</returns>
    /// exception cref="Exception">
    /// Thrown if the protocol conformance descriptor is not found in demangled results.
    /// </exception>
    public string GetProtocolConformanceDescriptor(SwiftTypeName implementingType, SwiftTypeName protocol)
    {
        if (!TryGetProtocolConformanceDescriptor(implementingType, protocol, out var symbol))
        {
            throw new Exception($"Protocol conformance descriptor not found for type '{implementingType}' and protocol '{protocol}'.");
        }

        return symbol;
    }

    /// <summary>
    /// Non-throwing variant of <see cref="GetProtocolConformanceDescriptor"/>. Returns false
    /// when the TBD contains no conformance descriptor for the requested (type, protocol) pair.
    /// Lets callers retry with an alternative implementing-type identity — notably for
    /// <c>@_originallyDefinedIn</c> umbrella re-exports where the TBD symbol is mangled with the
    /// type's ORIGINAL module (e.g. RealityKit) while the type decl is attributed to its current
    /// module (e.g. RealityFoundation) via its USR.
    /// </summary>
    public bool TryGetProtocolConformanceDescriptor(SwiftTypeName implementingType, SwiftTypeName protocol, out string symbol)
    {
        var protocolConformanceDescriptor = ProtocolConformanceDescriptors.FirstOrDefault(
            x => x.ImplementingType.Name == implementingType.ModuleQualifiedName && x.ProtocolType.Name == protocol.ModuleQualifiedName);

        if (protocolConformanceDescriptor == null)
        {
            symbol = string.Empty;
            return false;
        }

        symbol = protocolConformanceDescriptor.Symbol;
        return true;
    }
}
