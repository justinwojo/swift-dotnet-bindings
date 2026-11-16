// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// The wrapper slices of an Apple SDK framework. The generator is handed the framework as it sits in
    /// one SDK (its <c>.tbd</c> and module interface); the same framework sits at the same place in every
    /// other SDK of the platform, so each slice of the wrapper resolves it there.
    /// </summary>
    internal static class AppleFrameworkSlices
    {
        /// <summary>
        /// <paramref name="path"/>, inside <paramref name="fromSdkRoot"/>, at the same place in
        /// <paramref name="toSdkRoot"/>; null when it is not inside <paramref name="fromSdkRoot"/>.
        /// Symbolic links are resolved on both sides, so a path reached through the platform's
        /// unversioned SDK link maps as well as one through the versioned directory.
        /// </summary>
        internal static string? MapIntoSdk(string path, string fromSdkRoot, string toSdkRoot)
        {
            if (!SwiftWrapperLoweringEvidence.IsInside(path, fromSdkRoot))
                return null;
            var relative = Path.GetRelativePath(
                SwiftWrapperLoweringEvidence.RealPath(fromSdkRoot), SwiftWrapperLoweringEvidence.RealPath(path));
            return relative == "." ? toSdkRoot : Path.Combine(toSdkRoot, relative);
        }

        /// <summary>
        /// The module interface a compile for <paramref name="slice"/> reads in the framework at
        /// <paramref name="frameworkDirectory"/>: <c>Modules/&lt;M&gt;.swiftmodule/&lt;triple&gt;.swiftinterface</c>,
        /// the triple without a deployment version. A device SDK ships its frameworks' interfaces for
        /// <c>arm64e</c> alone, which swiftc reads for an <c>arm64</c> compile, so an <c>arm64</c> slice
        /// falls back to it. Null when the framework ships none for the slice.
        /// </summary>
        internal static string? InterfaceFor(string frameworkDirectory, string moduleName, SliceVariant slice)
        {
            var directory = Path.Combine(frameworkDirectory, "Modules", moduleName + ".swiftmodule");
            var candidates = slice.Architecture == "arm64" ? new[] { slice, slice with { Architecture = "arm64e" } } : [slice];
            return candidates
                .Select(c => Path.Combine(directory, c.GetTargetTriple(string.Empty) + ".swiftinterface"))
                .FirstOrDefault(File.Exists);
        }

        /// <summary>
        /// The resolution of the framework whose <c>.tbd</c> is <paramref name="tbdPath"/>, given in
        /// <paramref name="givenSdkRoot"/>, for <paramref name="slice"/>, whose SDK is
        /// <paramref name="sliceSdkRoot"/>. Every slice reads the one ABI descriptor the generator bound
        /// (<paramref name="abiJsonPath"/>), so no member is taken for simulator-only. Null, with
        /// <paramref name="why"/>, when the framework is not inside <paramref name="givenSdkRoot"/> or the
        /// slice's SDK lacks it or its interface.
        /// </summary>
        internal static XCFrameworkResolution? Resolve(
            string moduleName, string abiJsonPath, string tbdPath, string givenSdkRoot,
            SliceVariant slice, string sliceSdkRoot, out string? why)
        {
            why = null;
            if (MapIntoSdk(tbdPath, givenSdkRoot, sliceSdkRoot) is not { } sliceTbd)
            {
                why = $"'{tbdPath}' is not inside the SDK '{givenSdkRoot}'";
                return null;
            }
            if (!File.Exists(sliceTbd))
            {
                why = $"the {slice.SdkName} SDK has no '{sliceTbd}'";
                return null;
            }
            var frameworkDirectory = Path.GetDirectoryName(sliceTbd)!;
            if (InterfaceFor(frameworkDirectory, moduleName, slice) is not { } sliceInterface)
            {
                why = $"the {slice.SdkName} SDK's {moduleName}.framework ships no {slice.GetTargetTriple(string.Empty)} module interface";
                return null;
            }
            return new XCFrameworkResolution
            {
                AbiJsonPath = abiJsonPath,
                DylibPath = sliceTbd,
                TbdPath = sliceTbd,
                SwiftInterfacePath = sliceInterface,
                ModuleName = moduleName,
                XCFrameworkPath = frameworkDirectory,
                FrameworkSearchPath = Path.GetDirectoryName(frameworkDirectory)!,
                LibraryIdentifier = slice.SliceId,
                IsSimulatorSlice = slice.IsSimulator,
                SelectedArchitecture = slice.Architecture,
                SupportedArchitectures = [slice.Architecture],
            };
        }
    }
}
