// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// SandboxResultProbe carries its own `#nullable enable` (its reader returns string? for "no such
// file"), so the fakes below need the annotation too; this project builds with Nullable=disable +
// warnings-as-errors, where it would otherwise raise CS8632.
#nullable enable

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="SandboxResultProbe"/> — which of an app sandbox's candidate results files
/// belongs to the launch just attempted.
///
/// <para><b>The defect these pin.</b> The harness probes two container shapes because
/// <c>Environment.SpecialFolder.MyDocuments</c> does not resolve the same way on every Apple
/// platform: iOS puts the file under <c>Documents/</c>, tvOS under <c>Library/Caches/Documents/</c>.
/// The probe abandoned the whole search on the FIRST candidate that existed with a mismatched run
/// token, so a stale <c>Documents/</c> file left behind by an earlier iOS run masked the live tvOS
/// results — a complete, correctly-tokened file was discarded and the leg fell back to
/// console-scraping or a blind class skip.</para>
///
/// <para>The two properties that must hold together: a mismatched candidate disqualifies only ITSELF,
/// and a run with no matching candidate anywhere still recovers NOTHING. The second is the fail-closed
/// rule the token check exists for — the data container survives install, so scoring a stale file as
/// this launch's results is how a run that executed nothing reports green.</para>
/// </summary>
public class SandboxResultProbeTests
{
    const string Token = "0123456789abcdef0123456789abcdef";
    const string StaleToken = "ffffffffffffffffffffffffffffffff";

    const string IosPath = "/container/Documents/test-results.jsonl";
    const string TvOsPath = "/container/Library/Caches/Documents/test-results.jsonl";

    static readonly string[] Candidates = [IosPath, TvOsPath];

    static string Jsonl(string? runToken, string cls) =>
        (runToken == null ? "" : $"{{\"run_token\":\"{runToken}\"}}\n") +
        $"{{\"class\":\"{cls}\",\"test\":\"One\",\"status\":\"pass\",\"ms\":3}}\n" +
        $"{{\"class_done\":\"{cls}\",\"tests_run\":1}}\n" +
        "{\"done\":true,\"total\":1,\"passed\":1,\"failed\":0,\"skipped\":0}\n";

    /// <summary>A sandbox holding exactly the named files; anything else "does not exist".</summary>
    static System.Func<string, string?> Sandbox(Dictionary<string, string> files) =>
        path => files.TryGetValue(path, out var content) ? content : null;

    // ===================================================================
    //  A mismatched candidate disqualifies only itself
    // ===================================================================

    /// <summary>
    /// The exact shape the defect produced: a stale iOS-layout file sitting in front of the live
    /// tvOS-layout result. The old probe returned null here and threw the real results away.
    /// </summary>
    [Fact]
    public void StaleFirstCandidateDoesNotHideAMatchingSecondCandidate()
    {
        var outcome = SandboxResultProbe.Probe(Candidates, Token, Sandbox(new()
        {
            [IosPath] = Jsonl(StaleToken, "StaleFromAnEarlierRun"),
            [TvOsPath] = Jsonl(Token, "LiveThisRun"),
        }));

        Assert.Equal(TvOsPath, outcome.AcceptedPath);
        Assert.Contains("LiveThisRun", outcome.Content);
    }

    /// <summary>
    /// A stale candidate is still reported even when a later one is accepted — the operator has a
    /// leftover container and the log has to say so, or the next false-green investigation starts blind.
    /// </summary>
    [Fact]
    public void ARejectedCandidateIsStillReportedWhenALaterOneIsAccepted()
    {
        var outcome = SandboxResultProbe.Probe(Candidates, Token, Sandbox(new()
        {
            [IosPath] = Jsonl(StaleToken, "StaleFromAnEarlierRun"),
            [TvOsPath] = Jsonl(Token, "LiveThisRun"),
        }));

        var stale = Assert.Single(outcome.Rejected);
        Assert.Equal(IosPath, stale.Path);
        Assert.Equal(StaleToken, stale.ActualToken);
    }

    [Fact]
    public void AnUntokenedCandidateIsRejectedAndNamedAsCarryingNoToken()
    {
        var outcome = SandboxResultProbe.Probe(Candidates, Token, Sandbox(new()
        {
            [IosPath] = Jsonl(null, "PreTokenArtifact"),
            [TvOsPath] = Jsonl(Token, "LiveThisRun"),
        }));

        Assert.Equal(TvOsPath, outcome.AcceptedPath);
        Assert.Equal("<none>", Assert.Single(outcome.Rejected).ActualToken);
    }

    // ===================================================================
    //  Fail-closed: no match anywhere recovers nothing
    // ===================================================================

    [Fact]
    public void NoCandidateCarryingTheTokenRecoversNothing()
    {
        var outcome = SandboxResultProbe.Probe(Candidates, Token, Sandbox(new()
        {
            [IosPath] = Jsonl(StaleToken, "StaleA"),
            [TvOsPath] = Jsonl(StaleToken, "StaleB"),
        }));

        Assert.Null(outcome.Content);
        Assert.Null(outcome.AcceptedPath);
        Assert.Equal(2, outcome.Rejected.Count);
    }

    [Fact]
    public void AnEmptySandboxRecoversNothingAndRejectsNothing()
    {
        var outcome = SandboxResultProbe.Probe(Candidates, Token, Sandbox(new()));

        Assert.Null(outcome.Content);
        Assert.Empty(outcome.Rejected);
    }

    // ===================================================================
    //  Ordering and laziness
    // ===================================================================

    [Fact]
    public void TheFirstMatchingCandidateWins()
    {
        var outcome = SandboxResultProbe.Probe(Candidates, Token, Sandbox(new()
        {
            [IosPath] = Jsonl(Token, "FirstShape"),
            [TvOsPath] = Jsonl(Token, "SecondShape"),
        }));

        Assert.Equal(IosPath, outcome.AcceptedPath);
        Assert.Contains("FirstShape", outcome.Content);
        Assert.Empty(outcome.Rejected);
    }

    [Fact]
    public void ProbingStopsAtTheAcceptedCandidate()
    {
        // Reading a candidate costs a container access (a device leg pays a devicectl copy for it),
        // so a hit must not go on to touch the remaining paths.
        var read = new List<string>();

        SandboxResultProbe.Probe(Candidates, Token, path =>
        {
            read.Add(path);
            return path == IosPath ? Jsonl(Token, "FirstShape") : Jsonl(Token, "SecondShape");
        });

        Assert.Equal([IosPath], read);
    }

    // ===================================================================
    //  The app's announced results path (simctl container-lookup fallback)
    // ===================================================================

    const string Udid = "6FF5E5AE-C066-4C73-B1AE-D4927E7586BE";
    const string AnnouncedContainer =
        "/Users/runner/Library/Developer/CoreSimulator/Devices/" + Udid +
        "/data/Containers/Data/Application/E82EE369-2159-4CAE-8A48-7B8DA4A7283E";
    const string AnnouncedIosPath = AnnouncedContainer + "/Documents/test-results.jsonl";

    /// <summary>The line exactly as the iOS RuntimeTestsApp logs it through NSLog.</summary>
    static string AppOutputAnnouncing(string path) =>
        "2026-11-11 18:11:12.600 RuntimeTestsApp[65866:169926] [0.001s] [INFO] Starting\n" +
        $"2026-11-11 18:11:12.701 RuntimeTestsApp[65866:169926] [0.095s] [INFO] JSONL output: {path}\r\n" +
        "2026-11-11 18:14:23.759 RuntimeTestsApp[65866:169926] RESULTS FLUSHED\n" +
        "2026-11-11 18:14:23.759 RuntimeTestsApp[65866:169926] TEST SUCCESS\n";

    [Fact]
    public void TheAnnouncedPathIsParsedFromTheAppsLogLine()
    {
        Assert.Equal(AnnouncedIosPath,
            SandboxResultProbe.ParseAnnouncedResultsPath(AppOutputAnnouncing(AnnouncedIosPath), Udid));
    }

    [Fact]
    public void TheTvOSAnnouncedShapeIsAccepted()
    {
        var tvPath = AnnouncedContainer + "/Library/Caches/Documents/test-results.jsonl";
        Assert.Equal(tvPath, SandboxResultProbe.ParseAnnouncedResultsPath(AppOutputAnnouncing(tvPath), Udid));
    }

    /// <summary>
    /// The announcement is read off the app's console and then opened on the host, so only a path
    /// inside THIS simulator's app data containers, naming the results file, is ever read.
    /// </summary>
    [Theory]
    [InlineData("test-results.jsonl")] // relative
    [InlineData("/Users/runner/Library/Developer/CoreSimulator/Devices/00000000-0000-0000-0000-000000000000/data/Containers/Data/Application/X/Documents/test-results.jsonl")] // another simulator
    [InlineData("/var/mobile/Containers/Data/Application/X/Documents/test-results.jsonl")] // a physical device's path
    [InlineData(AnnouncedContainer + "/Documents/other.jsonl")] // not the results file
    [InlineData(AnnouncedContainer + "/../../../../../../etc/test-results.jsonl")] // climbs out
    public void AnAnnouncementOutsideThisSimulatorsContainersIsIgnored(string path)
    {
        Assert.Null(SandboxResultProbe.ParseAnnouncedResultsPath(AppOutputAnnouncing(path), Udid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RESULTS FLUSHED\nTEST SUCCESS\n")]
    public void OutputWithoutAnAnnouncementYieldsNoPath(string? output)
    {
        Assert.Null(SandboxResultProbe.ParseAnnouncedResultsPath(output, Udid));
    }

    [Fact]
    public void ContainerCandidatesComeFirstAndTheAnnouncementIsNotDuplicated()
    {
        var both = SandboxResultProbe.SimulatorCandidates(AnnouncedContainer, AnnouncedIosPath);
        Assert.Equal(
            [AnnouncedIosPath, AnnouncedContainer + "/Library/Caches/Documents/test-results.jsonl"],
            both);

        var elsewhere = "/Users/runner/Library/Developer/CoreSimulator/Devices/" + Udid +
            "/data/Containers/Data/Application/NEW/Documents/test-results.jsonl";
        Assert.Equal(elsewhere, SandboxResultProbe.SimulatorCandidates(AnnouncedContainer, elsewhere)[^1]);
        Assert.Equal(3, SandboxResultProbe.SimulatorCandidates(AnnouncedContainer, elsewhere).Count);
    }

    /// <summary>
    /// The CI false red this fallback exists for: <c>get_app_container</c> returned nothing after a
    /// completed run, so the announced path is the only candidate — and the run's file is recovered.
    /// </summary>
    [Fact]
    public void AFailedContainerLookupStillRecoversTheAnnouncedFile()
    {
        var candidates = SandboxResultProbe.SimulatorCandidates(null, AnnouncedIosPath);

        var outcome = SandboxResultProbe.Probe(candidates, Token, Sandbox(new()
        {
            [AnnouncedIosPath] = Jsonl(Token, "LiveThisRun"),
        }));

        Assert.Equal(AnnouncedIosPath, outcome.AcceptedPath);
        Assert.Contains("LiveThisRun", outcome.Content);
    }

    /// <summary>
    /// The fallback does not relax attribution: a stale file at the announced path (an earlier
    /// launch's results in the surviving container) is still rejected and nothing is recovered.
    /// </summary>
    [Fact]
    public void AStaleFileAtTheAnnouncedPathIsStillRejected()
    {
        var candidates = SandboxResultProbe.SimulatorCandidates(null, AnnouncedIosPath);

        var outcome = SandboxResultProbe.Probe(candidates, Token, Sandbox(new()
        {
            [AnnouncedIosPath] = Jsonl(StaleToken, "StaleFromAnEarlierRun"),
        }));

        Assert.Null(outcome.Content);
        Assert.Equal(StaleToken, Assert.Single(outcome.Rejected).ActualToken);
    }

    [Fact]
    public void NoContainerAndNoAnnouncementMeansNoCandidates()
    {
        Assert.Empty(SandboxResultProbe.SimulatorCandidates(null, null));
    }
}
