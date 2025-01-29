using BindingsGeneration.Demangling;
using Xamarin;

/// <summary>
/// A class to contain results from demangling the set of symbols in a MachO file.
/// </summary>
public class DemanglingResults
{
    /// <summary>
    /// Constructs a DemanglingResults object with the desired reductions separated out
    /// </summary>
    /// <param name="reductions">An array of reductions</param>
    DemanglingResults(IReduction[] reductions)
    {
        Errors = ArrayOf<ReductionError>(reductions);
        MetadataAccessors = ArrayOf<MetadataAccessorReduction>(reductions);
        DispatchThunks = ArrayOf<DispatchThunkFunctionReduction>(reductions);
        ProtocolWitnessTables = ArrayOf<ProtocolWitnessTableReduction>(reductions);
        ProtocolConformanceDescriptors = ArrayOf<ProtocolConformanceDescriptorReduction>(reductions);
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
    /// All protocol conformance descriptors founc while demangling symbols
    /// </summary>
    public ProtocolConformanceDescriptorReduction[] ProtocolConformanceDescriptors { get; private set; }

    /// <summary>
    /// Factory method to generate a suite of demangling results from the set of MachO files with the given target
    /// </summary>
    /// <param name="machOFiles">The MachO files to demangle</param>
    /// <param name="target">The target Abi to demangle from</param>
    /// <returns>A set of demangling results</returns>
    public static DemanglingResults FromMachOFiles(IEnumerable<MachOFile> machOFiles, Abi target)
    {
        var nlistEntries = machOFiles.PublicSymbols(target);
        var demangler = new Swift5Demangler();
        var allReductions = nlistEntries.Select(nle => nle.str).Where(Swift5Demangler.IsSwiftSymbol).Select(demangler.Run).ToArray();
        return new DemanglingResults(allReductions);
    }

    /// <summary>
    /// Async factory method to generate a suite of demangling results from the set of MachO files with the given target
    /// </summary>
    /// <param name="machOFiles"></param>
    /// <param name="target"></param>
    /// <param name="machOFiles">The MachO files to demangle</param>
    /// <param name="target">The target Abi to demangle from</param>
    /// <returns>A set of demangling results</returns>
    public static async Task<DemanglingResults> FromMachOFilesAsync(IEnumerable<MachOFile> machOFiles, Abi target)
    {
        return await Task.Run(() => FromMachOFiles(machOFiles, target));
    }

    /// <summary>
    /// Factory method to generate a suite of demangling results from the given file with the given target.
    /// </summary>
    /// <param name="path">Path to the file while contains symbols</param>
    /// <param name="target">The target Abi to demangle from</param>
    /// <returns>A set of demangling results</returns>
    public static DemanglingResults FromFile(string path, Abi target)
    {
        var files = MachO.Read(path);
        return FromMachOFiles(files, target);
    }

    /// <summary>
    /// Async factory method to generate a suite of demangling results from the given file with the given target.
    /// </summary>
    /// <param name="path">Path to the file while contains symbols</param>
    /// <param name="target">The target Abi to demangle from</param>
    /// <returns>A set of demangling results</returns>
    public static async Task<DemanglingResults> FromFileAsync(string path, Abi target)
    {
        return await Task.Run(() => FromFile(path, target));
    }
}
