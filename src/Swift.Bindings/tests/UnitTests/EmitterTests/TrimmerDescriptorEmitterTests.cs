// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="TrimmerDescriptorEmitter"/> — the per-module
/// <c>ILLink.Descriptors.xml</c> writer that closes the NativeAOT trimming gap
/// for generated open-generic ISwiftObject types. The descriptor is load-bearing
/// alongside the eager cctor; these tests guard the wire-format invariants ILC depends on.
/// </summary>
public class TrimmerDescriptorEmitterTests
{
    [Fact]
    public void Emit_NoOpenGenerics_ReturnsFalseAndWritesNoFile()
    {
        // Module with zero open-generic ISwiftObject types must not write a dead
        // descriptor file — the csproj wiring is Exists()-gated, and an empty
        // descriptor would still embed in the assembly + root a no-op in ILC.
        var dir = CreateTempDir();
        try
        {
            var ctx = new ModuleEmissionContext { ResolvedNamespace = "MyMod" };

            var wrote = TrimmerDescriptorEmitter.Emit(ctx, dir, "MyMod.Swift.iOS", NullLogger.Instance);

            Assert.False(wrote);
            Assert.False(File.Exists(Path.Combine(dir, TrimmerDescriptorEmitter.FileName)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Emit_OpenGenerics_WritesFileWithExpectedShape()
    {
        var dir = CreateTempDir();
        try
        {
            var ctx = new ModuleEmissionContext { ResolvedNamespace = "SwiftBindingsTestLib" };
            ctx.RecordOpenGenericISwiftObjectType("BlittableElementBuffer", arity: 1);
            ctx.RecordOpenGenericISwiftObjectType("Pair", arity: 2);

            var wrote = TrimmerDescriptorEmitter.Emit(
                ctx, dir, "SwiftBindingsTestLib.Swift.iOS", NullLogger.Instance);

            Assert.True(wrote);
            var path = Path.Combine(dir, TrimmerDescriptorEmitter.FileName);
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);

            // Linker root: <linker><assembly fullname="..."> with one <type> per open generic.
            Assert.Contains("<linker>", content);
            Assert.Contains("<assembly fullname=\"SwiftBindingsTestLib.Swift.iOS\">", content);

            // Backtick-arity is the CLR metadata convention ILC matches on; the
            // namespace comes from ResolvedNamespace, the arity from the recorder.
            Assert.Contains(
                "<type fullname=\"SwiftBindingsTestLib.BlittableElementBuffer`1\" preserve=\"all\" />",
                content);
            Assert.Contains(
                "<type fullname=\"SwiftBindingsTestLib.Pair`2\" preserve=\"all\" />",
                content);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Emit_OrdersEntriesDeterministically()
    {
        // The recorder uses a SortedDictionary; deterministic emit order matters because
        // the descriptor lands in source-controlled output (NUKE pack) and any diff churn
        // makes review noise.
        var dir = CreateTempDir();
        try
        {
            var ctx = new ModuleEmissionContext { ResolvedNamespace = "Mod" };
            ctx.RecordOpenGenericISwiftObjectType("Zebra", arity: 1);
            ctx.RecordOpenGenericISwiftObjectType("Alpha", arity: 1);
            ctx.RecordOpenGenericISwiftObjectType("Mango", arity: 1);

            TrimmerDescriptorEmitter.Emit(ctx, dir, "Mod.Swift.iOS", NullLogger.Instance);
            var content = File.ReadAllText(Path.Combine(dir, TrimmerDescriptorEmitter.FileName));

            var iAlpha = content.IndexOf("Alpha", StringComparison.Ordinal);
            var iMango = content.IndexOf("Mango", StringComparison.Ordinal);
            var iZebra = content.IndexOf("Zebra", StringComparison.Ordinal);
            Assert.True(iAlpha >= 0 && iMango > iAlpha && iZebra > iMango,
                $"Expected ordinal ordering Alpha < Mango < Zebra, got indices {iAlpha}/{iMango}/{iZebra}");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Emit_MissingResolvedNamespace_Throws()
    {
        // The descriptor names types by fullname; without a resolved namespace the writer
        // would silently emit unqualified names that ILC could not match against the
        // generated assembly. Fail loud instead.
        var dir = CreateTempDir();
        try
        {
            var ctx = new ModuleEmissionContext { ResolvedNamespace = null };
            ctx.RecordOpenGenericISwiftObjectType("Box", arity: 1);

            Assert.Throws<InvalidOperationException>(() =>
                TrimmerDescriptorEmitter.Emit(ctx, dir, "Mod.Swift.iOS", NullLogger.Instance));
            Assert.False(File.Exists(Path.Combine(dir, TrimmerDescriptorEmitter.FileName)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Emit_NullArguments_Throws()
    {
        var ctx = new ModuleEmissionContext { ResolvedNamespace = "Mod" };
        ctx.RecordOpenGenericISwiftObjectType("Box", arity: 1);

        Assert.Throws<ArgumentNullException>(() =>
            TrimmerDescriptorEmitter.Emit(null!, "/tmp", "asm", NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            TrimmerDescriptorEmitter.Emit(ctx, "", "asm", NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            TrimmerDescriptorEmitter.Emit(ctx, "/tmp", "", NullLogger.Instance));
    }

    [Fact]
    public void Emit_NestedOpenGeneric_UsesMetadataSlashNotDot()
    {
        // ILLink reads `fullname` as a CLR metadata type identifier — nested types
        // use '/' between outer and nested, not '.' (C# source syntax). The recorder
        // keys nested entries as "Outer.Inner" (joined by GetQualifiedTypeName); the
        // descriptor emitter must convert that segment to "Outer/Inner" so ILC can
        // resolve the type. Namespace separator stays '.'.
        var dir = CreateTempDir();
        try
        {
            var ctx = new ModuleEmissionContext { ResolvedNamespace = "MyMod" };
            ctx.PushTypeNesting("Outer");
            ctx.RecordOpenGenericISwiftObjectType("Box", arity: 1);
            ctx.PopTypeNesting();

            TrimmerDescriptorEmitter.Emit(ctx, dir, "MyMod.Swift.iOS", NullLogger.Instance);
            var content = File.ReadAllText(Path.Combine(dir, TrimmerDescriptorEmitter.FileName));

            Assert.Contains(
                "<type fullname=\"MyMod.Outer/Box`1\" preserve=\"all\" />",
                content);
            Assert.DoesNotContain("MyMod.Outer.Box`1", content);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Emit_FileName_IsCanonicalIlLinkDescriptorName()
    {
        // ILC and the trim analyzer both look for the literal file name "ILLink.Descriptors.xml"
        // when wired via TrimmerRootDescriptor; renaming this constant in the emitter must be
        // a deliberate change reflected in the csproj template, so pin it here.
        Assert.Equal("ILLink.Descriptors.xml", TrimmerDescriptorEmitter.FileName);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tde_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
