// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.WindowsPathGuard.cs — Windows MAX_PATH (260) regression guards
//
// Issue #40: long universal-slice paths inside the packed
//   runtimes/native/<Module>.xcframework/<slice>/<Module>.framework/Modules/
//   <Module>.swiftmodule/<triple>.<ext>
// layout silently fail to extract during `dotnet restore` on Windows hosts once the full
// restore-destination path reaches the legacy 260-char MAX_PATH ceiling. The module name
// appears 3x per path, so a long name pushed the build-critical .abi.json files over the
// line on the universal (arm64+x86_64) slices; those files never landed and the binding
// build failed for every Windows consumer of the latest Apple SDK.
//
// Two complementary gates model the default restore destination
//   C:\Users\<user>\.nuget\packages\<id-lower>\<version>\<packed-entry>
// and fail if any packed entry would push that path to/over the limit:
//   * AssertAppleXcframeworkWindowsPathSafe — an EARLY tripwire fired the moment the
//     xcframework is produced (the only point a path-length regression can be introduced),
//     so it covers every flow that (re)builds the framework, not just `nuke pack`.
//   * AssertProducedNupkgsWindowsPathSafe — the AUTHORITATIVE ship gate over every nupkg THIS
//     run produces, each checked against a budget derived from its real id+version (stale
//     packages left in the output folder by earlier runs are ignored).

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    const int WindowsMaxPath = 260;        // legacy ceiling; usable path length is 259
    const int AssumedMaxUserProfile = 40;  // C:\Users\<up to this many chars>\ — well past any real username

    // The SwiftBindings.Apple train ships as X.Y.Z (e.g. 26.2.5). The build-time tripwire
    // runs before --apple-version is known, so it budgets this fixed version length; the pack
    // gate re-checks every produced nupkg with its real version as the authoritative per-release
    // check, so a longer version still can't slip a too-long path through to consumers.
    const int AssumedMaxAppleVersionLength = 8;
    const string AppleSupplementPackageId = "SwiftBindings.Apple";

    // Longest package-relative entry path that still restores under the Windows MAX_PATH ceiling
    // for a nupkg whose "<id>.<version>" stem is `stemLength` chars. The restore folder
    // "<id-lower>\<version>" has the same length as the stem (lowercasing and the '.'->'\'
    // swap both preserve length), plus one trailing separator before the entry.
    int WindowsRestoreEntryBudget(int stemLength)
    {
        int prefix = @"C:\Users\".Length + AssumedMaxUserProfile + @"\.nuget\packages\".Length + stemLength + 1;
        return WindowsMaxPath - 1 - prefix;
    }

    // Authoritative ship gate: every entry of every nupkg THIS pack run produced in `outputDir`.
    // outputDir (e.g. /tmp/swift-nuget) is not cleaned between runs, so a stale nupkg from an
    // earlier version — or one skipped this run via --skip-apple — must not fail a build it did
    // not come from. Gate only files written at/after `producedAfterUtc` (captured just before
    // packing began); this guards what we ship, not whatever happens to be lying in the folder.
    void AssertProducedNupkgsWindowsPathSafe(AbsolutePath outputDir, System.DateTime producedAfterUtc)
    {
        int skippedStale = 0;
        foreach (var nupkg in Directory.GetFiles(outputDir, "*.nupkg").OrderBy(p => p))
        {
            if (File.GetLastWriteTimeUtc(nupkg) < producedAfterUtc) { skippedStale++; continue; }
            string stem = Path.GetFileNameWithoutExtension(nupkg);  // "<id>.<version>"
            int budget = WindowsRestoreEntryBudget(stem.Length);
            using var archive = ZipFile.OpenRead(nupkg);
            var offenders = archive.Entries
                .Where(e => e.FullName.Length > budget)
                .OrderByDescending(e => e.FullName.Length)
                .Select(e => $"  [{e.FullName.Length} > {budget}] {e.FullName}")
                .ToList();
            if (offenders.Count > 0)
                throw new System.InvalidOperationException(
                    $"Windows MAX_PATH guard: {offenders.Count} packed path(s) in {stem} would exceed the " +
                    $"Windows {WindowsMaxPath}-char limit on `dotnet restore` for a {AssumedMaxUserProfile}-char " +
                    $"user profile (issue #40). Shorten the packed layout:\n" + string.Join("\n", offenders));
            Log.Information(
                "Windows MAX_PATH guard: {Stem} OK — longest entry {Max}/{Budget} chars.",
                stem, archive.Entries.Max(e => e.FullName.Length), budget);
        }
        if (skippedStale > 0)
            Log.Information(
                "Windows MAX_PATH guard: ignored {Stale} stale nupkg(s) in {Dir} not produced by this run.",
                skippedStale, outputDir);
    }

    // Early tripwire: the Apple xcframework's worst-case packed entries, checked the moment the
    // framework is produced — the single point a path-length regression can enter. Called by every
    // flow that builds the xcframework (the BuildAppleSupplementXcframework target and the x64 gates
    // that call RunBuildAppleSupplementXcframework directly) and re-asserted by the gates that pack
    // the already-built xcframework without rebuilding it (PackGate, BehaviorTier) — all before pack.
    // Version-independent (it budgets a fixed assumed version), so a gate's throwaway version can't
    // false-fail it.
    void AssertAppleXcframeworkWindowsPathSafe(AbsolutePath xcframeworkDir, string moduleName)
    {
        if (!Directory.Exists(xcframeworkDir))
            throw new System.InvalidOperationException(
                $"Windows MAX_PATH guard: expected xcframework at '{xcframeworkDir}' but it is missing.");

        // Mirrors Swift.Bindings.Apple.csproj: native/<Module>.xcframework/** packs to
        // runtimes/native/<Module>.xcframework/**. Budget for the worst realistic release version.
        string packPrefix = $"runtimes/native/{moduleName}.xcframework/";
        int stemLength = AppleSupplementPackageId.Length + 1 + AssumedMaxAppleVersionLength;
        int budget = WindowsRestoreEntryBudget(stemLength);

        var offenders = new List<(int Length, string Path)>();
        int longest = 0;
        foreach (var file in Directory.EnumerateFiles(xcframeworkDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(xcframeworkDir, file).Replace('\\', '/');
            int entryLength = packPrefix.Length + rel.Length;
            longest = System.Math.Max(longest, entryLength);
            if (entryLength > budget)
                offenders.Add((entryLength, packPrefix + rel));
        }

        if (offenders.Count > 0)
            throw new System.InvalidOperationException(
                $"Windows MAX_PATH guard (xcframework build): {offenders.Count} packed path(s) for module " +
                $"'{moduleName}' would exceed the Windows {WindowsMaxPath}-char limit on `dotnet restore` for a " +
                $"{AssumedMaxUserProfile}-char user profile + {AssumedMaxAppleVersionLength}-char version (issue #40). " +
                "The module name appears 3x in every xcframework path — shorten AppleSupplementModuleName in " +
                "Build.AppleSupplement.cs:\n" +
                string.Join("\n", offenders.OrderByDescending(o => o.Length).Select(o => $"  [{o.Length} > {budget}] {o.Path}")));

        Log.Information(
            "Windows MAX_PATH guard (xcframework build): OK — longest packed entry {Max}/{Budget} chars " +
            "(reserves a {Profile}-char profile + {Ver}-char version; pack re-checks each nupkg with its real version).",
            longest, budget, AssumedMaxUserProfile, AssumedMaxAppleVersionLength);
    }
}
