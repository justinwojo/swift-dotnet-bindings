// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Answers one question about a bound module: is a type with the module's own name already
    /// visible in the generated wrapper's ambient scope, before the module is even imported?
    ///
    /// When it is, the wrapper cannot reach the module by name. Swift resolves the leading
    /// identifier of <c>Name.X</c> as a type whenever one is in scope and only falls back to
    /// module lookup otherwise, so every <c>Name.</c> qualifier the emitter writes is read as
    /// member lookup into the shadowing type and fails. The SDK owns a surprising number of such
    /// names — a POSIX overlay alias, for one, makes <c>Semaphore</c> a type on every Apple
    /// platform, which is enough to break any module that calls itself Semaphore.
    ///
    /// The check is a typecheck of two lines against the SDK, which is authoritative in a way a
    /// curated list of SDK type names cannot be: it IS swiftc's name lookup, so it stays correct
    /// as SDKs add names, and it cannot claim a shadow that the real wrapper compile would not
    /// also hit. It costs one short-lived frontend invocation (tens of milliseconds — nothing is
    /// imported beyond Foundation and nothing is emitted).
    ///
    /// Only the ambient scope is probed: Foundation transitively brings in Swift, Darwin, and
    /// ObjectiveC, which is what every generated wrapper imports unconditionally. A name shadowed
    /// solely by an optional dependency is a different question, answered by
    /// <see cref="DepModuleCollisionDetector"/>.
    /// </summary>
    internal static class ModuleNameShadowProbe
    {
        private const string ProbeAliasName = "_SBWShadowProbe";

        /// <summary>
        /// True when a type named <paramref name="moduleName"/> resolves against the SDK alone.
        ///
        /// Fails open — every failure to run the probe answers "not shadowed", which preserves the
        /// module-qualified emission that is correct for all but this narrow shape. Note the
        /// asymmetry that makes fail-open the safe default here: a missed shadow costs the
        /// pre-existing "has no member" wrapper error, while a spurious one would strip qualifiers
        /// from a module that needs them and reintroduce ambiguity across the whole binding.
        /// </summary>
        internal static bool IsModuleNameShadowedBySdk(
            string moduleName,
            PlatformInfo platformInfo,
            ICommandRunner commandRunner,
            ILogger logger)
        {
            if (!IsSwiftIdentifier(moduleName))
                return false;

            string? probeDir = null;
            try
            {
                var slice = platformInfo.GetSlice(isSimulator: true);
                var sdkPath = SwiftWrapperCompiler.ResolveSdkPath(slice.SdkName, commandRunner);
                if (string.IsNullOrEmpty(sdkPath))
                    return false;

                probeDir = Path.Combine(Path.GetTempPath(), "sbw-shadow-probe-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(probeDir);
                var probeFile = Path.Combine(probeDir, "ShadowProbe.swift");

                // The alias resolves iff a type of that name is already in scope. Deliberately does
                // NOT import the bound module: the point is to see the scope the module's own name
                // has to compete with.
                File.WriteAllText(probeFile,
                    "import Foundation\n" +
                    $"private typealias {ProbeAliasName} = {moduleName}\n");

                var triple = slice.GetTargetTriple(platformInfo.DefaultMinimumOS);
                var (exitCode, _, _) = commandRunner.Run(
                    "xcrun",
                    $"swift-frontend -typecheck -sdk \"{sdkPath}\" -target {triple} \"{probeFile}\"");

                if (exitCode == 0)
                {
                    logger.LogInformation(
                        "Module name '{Module}' is shadowed by an SDK type of the same name; the wrapper cannot qualify through it.",
                        moduleName);
                    return true;
                }
                return false;
            }
            // InvalidOperationException is how SwiftWrapperCompiler.ResolveSdkPath reports an
            // unresolvable SDK, and SystemCommandRunner.Run itself can throw Win32Exception (the
            // "xcrun" process failed to launch) or TimeoutException (the typecheck invocation
            // hung). None of these should propagate and abort generation over a question whose
            // "don't know" answer is already safe, so this catches broadly rather than
            // enumerating every ICommandRunner implementation's failure modes.
            catch (Exception ex)
            {
                logger.LogWarning("Could not probe whether module name '{Module}' is SDK-shadowed: {Message}",
                    moduleName, ex.Message);
                return false;
            }
            finally
            {
                if (probeDir != null)
                {
                    try { Directory.Delete(probeDir, recursive: true); }
                    catch (IOException) { /* temp dir; a leftover probe file is harmless */ }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        /// <summary>
        /// Guards the probe source against a module name that is not a bare Swift identifier —
        /// anything else would make the probe a syntax error and read as "not shadowed" for the
        /// wrong reason.
        /// </summary>
        private static bool IsSwiftIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (!char.IsLetter(name[0]) && name[0] != '_')
                return false;
            foreach (var c in name)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                    return false;
            }
            return true;
        }
    }
}
