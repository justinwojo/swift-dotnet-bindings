// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A declaration-record key read against the compiled assembly it describes: the merge tells a
/// respelled key's <c>Nullable&lt;T&gt;</c> apart from an annotation this way, and the compile gate's
/// completeness check enumerates what a record must cover.
/// </summary>
public class SwiftDeclarationRecordKeysTests
{
    private const string Api = """
        namespace Kit
        {
            public struct Point { public int X; }
            public sealed class Box
            {
                public Box(string? name) { }
                public string this[int? i, string? k] => "";
                public string this[int i, string k] => "";
                public void Load(string? a, int? b) { }
                public void Load(string a, int b) { }
                public void Take(ref string? text, out int count) { count = 0; }
                public void Pairs((int a, string? b) pair, (int, int, int, int, int, int, int, int) wide) { }
                public void Fill(System.Collections.Generic.IReadOnlyList<int?>? values, Point?[] points, Point[]? plain) { }
                public T Echo<T>(T? value) => value!;
                public static unsafe void Raw(delegate* unmanaged<nint, int> callback, byte* bytes) { }
                public int Count { get; set; }
                public sealed class Inner { public void Ping(global::System.Action<long, long>? done) { } }
                protected void Guarded() { }
                internal void Hidden() { }
            }
            public static class BoxExtensions { public static int Size(this Box box, params nint[] extra) => 0; }
            public interface IListener { string OnEvent(int value); }
            public sealed class Nest<T> { public sealed class Inner { } }
            public static class NestUse { public static void Hold(Nest<int>.Inner inner) { } }
        }
        """;

    private static SwiftDeclarationRecordKeys.AssemblyIndex Index() =>
        SwiftDeclarationRecordKeys.Index(SwiftTransitionManifestTests.Compile("Kit", Api), "Kit");

    [Theory]
    [InlineData("Box.Box(string?)", "Box..ctor(String~)")]
    [InlineData("Box.this[int?,string?]", "Box.Item[Int32?,String~]")]
    [InlineData("Box.this[int,string]", "Box.Item[Int32,String~]")]
    [InlineData("Box.Load(string?,int?)", "Box.Load(String~,Int32?)")]
    [InlineData("Box.Load(string,int?)", "Box.Load(String~,Int32?)")]
    [InlineData("Box.Load(string,int)", "Box.Load(String~,Int32)")]
    [InlineData("Box.Take(ref string?,out int)", "Box.Take(&String~,&Int32)")]
    [InlineData("Box.Pairs((int a, string? b),(int, int, int, int, int, int, int, int))", "Box.Pairs(ValueTuple<Int32,String~>,ValueTuple<Int32,Int32,Int32,Int32,Int32,Int32,Int32,Int32>)")]
    [InlineData("Box.Fill(System.Collections.Generic.IReadOnlyList<int?>?,Kit.Point?[],Point[]?)", "Box.Fill(IReadOnlyList<Int32?>~,Point?[]~,Point[]~)")]
    [InlineData("Box.Echo(T?)`1", "Box.Echo(T~)`1")]
    [InlineData("Box.Raw(delegate* unmanaged<nint, int>,byte*)", "Box.Raw(fnptr<IntPtr,Int32>,Byte*)")]
    [InlineData("Box.Inner.Ping(global::System.Action<long, long>?)", "Box.Inner.Ping(Action<Int64,Int64>~)")]
    [InlineData("BoxExtensions.Size(this Box,params nint[])", "BoxExtensions.Size(Box~,IntPtr[]~)")]
    [InlineData("IListener.OnEvent(int)", "IListener.OnEvent(Int32)")]
    [InlineData("Box.Guarded()", "Box.Guarded()")]
    // A type nested in a generic one carries the outer type's arguments, as metadata does.
    [InlineData("NestUse.Hold(Kit.Nest<int>.Inner)", "NestUse.Hold(Inner<Int32>~)")]
    public void AKey_ResolvesToTheOneMemberItSpells(string key, string member)
    {
        var parsed = SwiftDeclarationRecordKeys.Parse(key);
        Assert.NotNull(parsed);
        Assert.Equal(member, Assert.Single(Index().Resolve(parsed!)).ToString());
    }

    [Theory]
    // A value type's `T` without `?` is not `Nullable<T>`, nor is its `?` an annotation.
    [InlineData("Box.Load(string,long)")]
    [InlineData("Box.Fill(IReadOnlyList<int>,Point[],Point[])")]
    [InlineData("Box.Hidden()")]
    [InlineData("Box.Echo(U)`1")]
    [InlineData("Box.Missing(int)")]
    public void AKeyTheAssemblyDoesNotDeclare_ResolvesToNothing(string key) =>
        Assert.Empty(Index().Resolve(SwiftDeclarationRecordKeys.Parse(key)!));

    [Fact]
    public void ThreeKeysOfOneShape_NameTwoClrSignatures()
    {
        // `Load(string?,int?)` and `Load(string,int?)` are one signature; `Load(string,int)` is another.
        var index = Index();
        string Signature(string key) => SwiftDeclarationRecordKeys.ClrSignature(Assert.Single(index.Resolve(SwiftDeclarationRecordKeys.Parse(key)!)).Parameters);
        Assert.Equal(Signature("Box.Load(string?,int?)"), Signature("Box.Load(string,int?)"));
        Assert.NotEqual(Signature("Box.Load(string,int?)"), Signature("Box.Load(string,int)"));
    }

    [Theory]
    [InlineData("Box.Count")]
    [InlineData("Box.Inner")]
    public void APropertyOrTypeKey_ResolvesWithoutAParameterList(string key)
    {
        var parsed = SwiftDeclarationRecordKeys.Parse(key)!;
        Assert.Null(parsed.Parameters);
        Assert.Single(Index().Resolve(parsed));
    }

    [Theory]
    [InlineData("Box.Load(string")]
    [InlineData("Box.this][")]
    [InlineData("Box.Load(int)x")]
    [InlineData("Box.Load(<int>)")]
    public void AKeyThatDoesNotRead_IsNotParsed(string key) => Assert.Null(SwiftDeclarationRecordKeys.Parse(key));

    [Fact]
    public void TheIndex_ListsPublicAndProtectedMembers_NotInternalOnes()
    {
        var members = Index().Members.Select(m => m.ToString()).ToList();
        Assert.Contains("Box.Guarded()", members);
        Assert.DoesNotContain(members, m => m.Contains("Hidden"));
        Assert.Contains(Index().Types, t => t == "Box.Inner");
    }

    [Fact]
    public void AModuleFunctionKey_ResolvesInAHolderTheBindingProjectExtends_NotInARecordedSwiftClassOfItsName()
    {
        // A binding project may give the generated holder an interface and an instance helper in its own
        // partial declaration; a Swift class named like the holder is a type the record lists.
        var index = SwiftDeclarationRecordKeys.Index(SwiftTransitionManifestTests.Compile("Kit", """
            namespace Kit
            {
                public partial class Functions : System.IDisposable
                {
                    public static void Load(string path) { }
                    public void Dispose() { }
                    public int Helper() => 0;
                }
            }
            """), "Kit");
        var key = SwiftDeclarationRecordKeys.Parse("Load(string)")!;
        Assert.Equal("Functions.Load(String~)", Assert.Single(index.Resolve(key, "Kit")).ToString());
        Assert.Empty(index.Resolve(key, "Kit", new HashSet<string> { "Functions" }));
        Assert.Empty(index.Resolve(SwiftDeclarationRecordKeys.Parse("Helper()")!, "Kit"));
    }
}
