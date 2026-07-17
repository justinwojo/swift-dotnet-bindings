// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using BindingsGeneration.ObjC;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Ordering, de-duplication, and empty-set semantics of the clang <c>-F</c> search-path merge that
/// feeds the mixed-framework AST dump. Caller-supplied paths (sibling / dependency slices) must lead
/// so a deliberately provided dependency outranks an incidental embedded framework on a module-name
/// collision; auto-detected nested paths follow; and an empty merge returns <c>null</c> so the
/// invoker keeps its historical "no additional search paths" behavior. Absolute inputs keep the
/// <c>Path.GetFullPath</c> normalization idempotent, so the assertions are CWD-independent.
/// </summary>
public class MergeSearchPathsTests
{
    private static string Abs(params string[] parts) => Path.GetFullPath(Path.Combine(parts));

    [Fact]
    public void MergeSearchPaths_CallerPathsLeadNestedFollow()
    {
        var caller = new List<string> { Abs("/tmp", "dep-A"), Abs("/tmp", "dep-B") };
        var nested = new List<string> { Abs("/tmp", "embedded-C") };

        var merged = ObjCPipeline.MergeSearchPaths(caller, nested);

        Assert.NotNull(merged);
        Assert.Equal(
            new[] { Abs("/tmp", "dep-A"), Abs("/tmp", "dep-B"), Abs("/tmp", "embedded-C") },
            merged);
    }

    [Fact]
    public void MergeSearchPaths_DeDupesAcrossCallerAndNested_KeepingFirst()
    {
        var shared = Abs("/tmp", "shared");
        var caller = new List<string> { shared };
        var nested = new List<string> { shared, Abs("/tmp", "embedded") };

        var merged = ObjCPipeline.MergeSearchPaths(caller, nested);

        Assert.Equal(new[] { shared, Abs("/tmp", "embedded") }, merged);
    }

    [Fact]
    public void MergeSearchPaths_NullCaller_UsesNestedOnly()
    {
        var nested = new List<string> { Abs("/tmp", "embedded") };

        var merged = ObjCPipeline.MergeSearchPaths(null, nested);

        Assert.Equal(new[] { Abs("/tmp", "embedded") }, merged);
    }

    [Fact]
    public void MergeSearchPaths_EmptyMerge_ReturnsNull()
    {
        Assert.Null(ObjCPipeline.MergeSearchPaths(null, new List<string>()));
        Assert.Null(ObjCPipeline.MergeSearchPaths(new List<string>(), new List<string>()));
    }

    [Fact]
    public void MergeSearchPaths_SkipsNullAndEmptyEntries()
    {
        var caller = new List<string> { "", Abs("/tmp", "real") };

        var merged = ObjCPipeline.MergeSearchPaths(caller, new List<string>());

        Assert.Equal(new[] { Abs("/tmp", "real") }, merged);
    }
}
