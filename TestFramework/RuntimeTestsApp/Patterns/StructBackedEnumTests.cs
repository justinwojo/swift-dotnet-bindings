// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Patterns;

/// <summary>
/// Tests for struct-backed enum pattern: HttpVerb struct with static instances
/// (Get, Post, Put, Delete, Patch), string RawValue, equality operators, and
/// custom construction.
/// </summary>
public class StructBackedEnumTests : TestBase
{
    public StructBackedEnumTests(TestResults results) : base(results) { }

    #region Tier 2 — Static Instances and RawValue

    [TestTier(TestTier.Tier2)]
    public void TestGetRawValue()
    {
        var verb = HttpVerb.Get;
        var raw = verb.RawValue.ToString();
        AssertEqual("GET", raw, "HttpVerb.Get.RawValue");
        TestLogger.Info($"HttpVerb.Get.RawValue = \"{raw}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestPostRawValue()
    {
        var verb = HttpVerb.Post;
        var raw = verb.RawValue.ToString();
        AssertEqual("POST", raw, "HttpVerb.Post.RawValue");
        TestLogger.Info($"HttpVerb.Post.RawValue = \"{raw}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestPutRawValue()
    {
        var verb = HttpVerb.Put;
        var raw = verb.RawValue.ToString();
        AssertEqual("PUT", raw, "HttpVerb.Put.RawValue");
        TestLogger.Info($"HttpVerb.Put.RawValue = \"{raw}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestDeleteRawValue()
    {
        var verb = HttpVerb.Delete;
        var raw = verb.RawValue.ToString();
        AssertEqual("DELETE", raw, "HttpVerb.Delete.RawValue");
        TestLogger.Info($"HttpVerb.Delete.RawValue = \"{raw}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestPatchRawValue()
    {
        var verb = HttpVerb.Patch;
        var raw = verb.RawValue.ToString();
        AssertEqual("PATCH", raw, "HttpVerb.Patch.RawValue");
        TestLogger.Info($"HttpVerb.Patch.RawValue = \"{raw}\"");
    }

    #endregion

    #region Tier 2 — Equality Operators

    [TestTier(TestTier.Tier2)]
    public void TestEqualitySameInstance()
    {
        var a = HttpVerb.Get;
        var b = HttpVerb.Get;
        AssertTrue(a == b, "Get == Get");
        AssertFalse(a != b, "Get != Get should be false");
        TestLogger.Info("HttpVerb equality (same instance) passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestInequalityDifferentInstances()
    {
        var get = HttpVerb.Get;
        var post = HttpVerb.Post;
        AssertTrue(get != post, "Get != Post");
        AssertFalse(get == post, "Get == Post should be false");
        TestLogger.Info("HttpVerb inequality (different instances) passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestAllStaticInstancesDistinct()
    {
        var verbs = new[] { HttpVerb.Get, HttpVerb.Post, HttpVerb.Put, HttpVerb.Delete, HttpVerb.Patch };
        for (int i = 0; i < verbs.Length; i++)
        {
            for (int j = i + 1; j < verbs.Length; j++)
            {
                AssertTrue(verbs[i] != verbs[j],
                    $"{verbs[i].RawValue} != {verbs[j].RawValue}");
            }
        }
        TestLogger.Info("All HttpVerb static instances are distinct");
    }

    #endregion

    #region Tier 2 — Custom Construction

    [TestTier(TestTier.Tier2)]
    public void TestCustomConstruction()
    {
        var verb = new HttpVerb("OPTIONS");
        var raw = verb.RawValue.ToString();
        AssertEqual("OPTIONS", raw, "Custom HttpVerb RawValue");
        TestLogger.Info($"Custom HttpVerb: RawValue = \"{raw}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCustomEqualityWithStatic()
    {
        var custom = new HttpVerb("GET");
        var builtin = HttpVerb.Get;
        AssertTrue(custom == builtin, "Custom 'GET' == HttpVerb.Get");
        TestLogger.Info("Custom construction equality with static instance passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCustomInequalityWithStatic()
    {
        var custom = new HttpVerb("HEAD");
        var builtin = HttpVerb.Get;
        AssertTrue(custom != builtin, "Custom 'HEAD' != HttpVerb.Get");
        TestLogger.Info("Custom construction inequality with static instance passed");
    }

    #endregion
}
