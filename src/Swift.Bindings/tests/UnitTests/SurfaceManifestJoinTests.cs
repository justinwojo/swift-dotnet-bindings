// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The API-manifest join reads the generator's projected key against the emitted C#. Each case
/// takes its spellings from a real binding in the validation corpus (renamed into the fixture's
/// <c>TestModule</c> root): the producer writes the projection, the scan reads the file, and the
/// two must meet on the same member or fail the target closed.
/// </summary>
public class SurfaceManifestJoinTests
{
    private static SurfaceCoverageRecord OldCoverage(string source, params (string Signature, string Symbol)[] manifest)
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var request = fixture.Request(source, source, manifest, manifest);
        var result = SurfaceAccountingEngine.Analyze(request);
        return Assert.Single(result.Old.Coverage);
    }

    private static SurfaceAccountingResult Analyze(string source, params (string Signature, string Symbol)[] manifest)
    {
        using var fixture = new SurfaceAccountingTestFixture();
        return SurfaceAccountingEngine.Analyze(fixture.Request(source, source, manifest, manifest));
    }

    private static string StatusOf(SurfaceAccountingResult result, Func<SurfaceMemberObservation, bool> member)
        => result.Old.Members.Single(member).ApiManifest.Single().MatchStatus;

    [Fact]
    public void CaselessEnumNamespaceIsPartOfTheOwner()
    {
        // A Swift caseless enum (AES) is emitted as a C# namespace; the manifest owner keeps it.
        const string source = """
            namespace TestModule
            {
                namespace AES
                {
                    public static partial class GCM
                    {
                        public sealed class SealedBox { }
                        public static byte[] Open( global::TestModule.AES.GCM.SealedBox sealedBox,  global::TestModule.SymmetricKey key) => null!;
                    }
                }
                public sealed class SymmetricKey { }
            }
            """;
        var result = Analyze(source, ("AES.GCM.Open(TestModule.AES.GCM.SealedBox,TestModule.SymmetricKey)", "sym_open"));

        var coverage = Assert.Single(result.Old.Coverage);
        Assert.Empty(coverage.Errors);
        Assert.Equal(1, coverage.ManifestMatched);
        Assert.DoesNotContain(";join=", StatusOf(result, m => m.PublicKey.Name == "Open"), StringComparison.Ordinal);
    }

    [Fact]
    public void TypeNamedThroughUsingsMatchesFullyQualifiedEmission()
    {
        const string source = """
            namespace TestModule
            {
                namespace AES
                {
                    public static partial class GCM
                    {
                        public static void Seal( global::System.Collections.Generic.IEnumerable<byte> message,  global::TestModule.SymmetricKey key) { }
                    }
                }
                public sealed class SymmetricKey { }
            }
            """;
        var result = Analyze(source, ("AES.GCM.Seal(IEnumerable<byte>,TestModule.SymmetricKey)", "sym_seal"));

        Assert.Empty(Assert.Single(result.Old.Coverage).Errors);
        Assert.EndsWith(";join=qualification", StatusOf(result, m => m.PublicKey.Name == "Seal"), StringComparison.Ordinal);
    }

    [Fact]
    public void FreeFunctionMatchesItsContainerClassWithoutAnOwner()
    {
        // The module's free functions are emitted into a container class (Functions when the
        // module name would stutter); the producer writes no owner. The int convenience overload
        // the emitter adds next to the nint original stays unrecorded.
        const string source = """
            namespace TestModule
            {
                public partial class Functions
                {
                    public static string DatabaseQuestionMarks( nint count) => "";
                    public static string DatabaseQuestionMarks(int count) => DatabaseQuestionMarks((nint)count);
                }
            }
            """;
        var result = Analyze(source, ("DatabaseQuestionMarks(nint)", "sym_marks"));

        var coverage = Assert.Single(result.Old.Coverage);
        Assert.Empty(coverage.Errors);
        Assert.Equal(1, coverage.ManifestMatched);
        Assert.Single(result.Old.Members, m => m.PublicKey.Name == "DatabaseQuestionMarks" && m.ApiManifest.Count == 1
            && m.PublicKey.Parameters.Single().Type.Contains("nint", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceNullabilityTheProjectionDroppedStillMatches()
    {
        const string source = """
            namespace TestModule
            {
                public partial class Logger
                {
                    public static void Debug(object? message, string file, string function, nint line, object? context) { }
                }
            }
            """;
        var result = Analyze(source, ("Logger.Debug(object?,string,string,nint,object)", "sym_debug"));

        Assert.Empty(Assert.Single(result.Old.Coverage).Errors);
        Assert.EndsWith(";join=nullability", StatusOf(result, m => m.PublicKey.Name == "Debug"), StringComparison.Ordinal);
    }

    [Fact]
    public void ArityMarkerKeptForAnErasedOverloadMatchesTheNonGenericSibling()
    {
        // read<T>(_ block: (Database) throws -> T) is emitted as a generic Func overload and a
        // non-generic Action overload; the row describes the Action one but keeps the arity marker.
        const string source = """
            namespace TestModule
            {
                public sealed class Database { }
                public partial class DatabaseQueue
                {
                    public void Read(global::System.Action<global::TestModule.Database> block) { }
                    public T Read<T>(global::System.Func<global::TestModule.Database, T> block) => default!;
                }
            }
            """;
        var result = Analyze(source, ("DatabaseQueue.Read(Action<TestModule.Database>)`1", "sym_read"));

        Assert.Empty(Assert.Single(result.Old.Coverage).Errors);
        Assert.EndsWith(";join=generic-arity",
            StatusOf(result, m => m.PublicKey.Name == "Read" && m.PublicKey.GenericArity == 0),
            StringComparison.Ordinal);
        Assert.Empty(result.Old.Members.Single(m => m.PublicKey.Name == "Read" && m.PublicKey.GenericArity == 1).ApiManifest);
    }

    [Fact]
    public void UnresolvedTypeParameterAndCollectionInterfaceMatchTheirOwnOverloads()
    {
        // The projection spells a type parameter it could not resolve as Swift.AnyType and a Swift
        // array parameter as IEnumerable where the emitter wrote IReadOnlyList; the first
        // parameter still tells the two SetImage overloads apart.
        const string source = """
            namespace TestModule
            {
                public interface IResource { }
                public sealed class Source { }
                public sealed class Item { }
                public partial class Callable<TRequest, TResponse>
                {
                    public void Call(TRequest data, object? completion) { }
                }
                public partial class Wrapper<TBase>
                {
                    public void SetImage(IResource? resource, global::System.Collections.Generic.IReadOnlyList<global::TestModule.Item>? options) { }
                    public void SetImage(global::TestModule.Source? source, global::System.Collections.Generic.IReadOnlyList<global::TestModule.Item>? options) { }
                }
            }
            """;
        var result = Analyze(
            source,
            ("Callable.Call(Swift.AnyType,object?)`2", "sym_call"),
            ("Wrapper.SetImage(IResource,IEnumerable<TestModule.Item>)`1", "sym_resource"),
            ("Wrapper.SetImage(TestModule.Source,IEnumerable<TestModule.Item>)`1", "sym_source"));

        var coverage = Assert.Single(result.Old.Coverage);
        Assert.Empty(coverage.Errors);
        Assert.Equal(3, coverage.ManifestMatched);
        Assert.EndsWith(";join=projection", StatusOf(result, m => m.PublicKey.Name == "Call"), StringComparison.Ordinal);
        var resource = result.Old.Members.Single(m => m.PublicKey.Name == "SetImage"
            && m.PublicKey.Parameters[0].Type.Contains("IResource", StringComparison.Ordinal));
        Assert.Equal("sym_resource", resource.ApiManifest.Single().RawSymbol);
        var sourceOverload = result.Old.Members.Single(m => m.PublicKey.Name == "SetImage"
            && m.PublicKey.Parameters[0].Type.Contains("Source", StringComparison.Ordinal));
        Assert.Equal("sym_source", sourceOverload.ApiManifest.Single().RawSymbol);
    }

    [Fact]
    public void DeletedNativeWidthOverloadIsNotAbsorbedByItsConvenienceSibling()
    {
        // Only the int convenience overload is emitted; the row names the nint original. No tier
        // forgets nint versus int, so the row stays a phantom and the target fails closed.
        const string source = """
            namespace TestModule
            {
                public partial class Stream
                {
                    public void WriteUint16(int value) { }
                }
            }
            """;
        var coverage = OldCoverage(source, ("Stream.WriteUint16(nint)", "sym_write"));

        Assert.Equal(1, coverage.ManifestPhantom);
        Assert.Contains(coverage.Errors, e => e.Contains("phantom", StringComparison.Ordinal));
    }

    [Fact]
    public void RowWithTwoEquallyGoodCandidatesIsAmbiguous()
    {
        // The row's IEnumerable can stand for either emitted collection interface.
        const string source = """
            namespace TestModule
            {
                public partial class Store
                {
                    public void Put(global::System.Collections.Generic.IReadOnlyList<int> keys) { }
                    public void Put(global::System.Collections.Generic.IReadOnlyCollection<int> keys) { }
                }
            }
            """;
        var coverage = OldCoverage(source, ("Store.Put(IEnumerable<int>)", "sym_put"));

        Assert.Equal(1, coverage.ManifestAmbiguous);
        Assert.Equal(0, coverage.ManifestMatched);
        Assert.NotEmpty(coverage.Errors);
    }

    [Fact]
    public void TypeOfTheSameNameInAnotherNamespaceIsNotTheProjectedOne()
    {
        // The row's unqualified IEnumerable is the BCL interface; a library type of the same name
        // is a different parameter, so the only overload left is not the one the row describes.
        const string source = """
            namespace Other
            {
                public interface IEnumerable<T> { }
            }
            namespace TestModule
            {
                public sealed class Key { }
                public partial class Cache
                {
                    public void Put(global::Other.IEnumerable<global::TestModule.Key> keys) { }
                }
            }
            """;
        var coverage = OldCoverage(source, ("Cache.Put(IEnumerable<TestModule.Key>)", "sym_put"));

        Assert.Equal(1, coverage.ManifestPhantom);
        Assert.Equal(0, coverage.ManifestMatched);
    }

    [Fact]
    public void MemberOutsideTheManifestRootNamespaceIsNotACandidate()
    {
        // Same owner and name, but under another namespace: the producer of TestModule's manifest
        // cannot have described it.
        const string source = """
            namespace Elsewhere
            {
                public partial class Client
                {
                    public void Connect() { }
                }
            }
            """;
        var coverage = OldCoverage(source, ("Client.Connect()", "sym_connect"));

        Assert.Equal(1, coverage.ManifestPhantom);
        Assert.NotEmpty(coverage.Errors);
    }

    [Fact]
    public void LooserReadingThatAdmitsAnUnrecordedSiblingLeavesTheRowAmbiguous()
    {
        // The row names IEnumerable through the usings, so it settles only past exact; the
        // IReadOnlyList sibling has no row of its own and the projection tier would admit it too,
        // so the row could be describing either overload.
        const string source = """
            namespace TestModule
            {
                public sealed class Key { }
                public partial class Cache
                {
                    public void Put(global::System.Collections.Generic.IEnumerable<global::TestModule.Key> keys) { }
                    public void Put(global::System.Collections.Generic.IReadOnlyList<global::TestModule.Key> keys) { }
                }
            }
            """;
        var coverage = OldCoverage(source, ("Cache.Put(IEnumerable<TestModule.Key>)", "sym_put"));

        Assert.Equal(1, coverage.ManifestAmbiguous);
        Assert.Equal(0, coverage.ManifestMatched);
    }

    [Fact]
    public void SiblingsEachSettledByTheirOwnRowDoNotBlockEachOther()
    {
        const string source = """
            namespace TestModule
            {
                public sealed class Key { }
                public partial class Cache
                {
                    public void Put(global::System.Collections.Generic.IEnumerable<global::TestModule.Key> keys) { }
                    public void Put(global::System.Collections.Generic.IReadOnlyList<global::TestModule.Key> keys) { }
                }
            }
            """;
        var result = Analyze(
            source,
            ("Cache.Put(IEnumerable<TestModule.Key>)", "sym_enumerable"),
            ("Cache.Put(IReadOnlyList<TestModule.Key>)", "sym_list"));

        var coverage = Assert.Single(result.Old.Coverage);
        Assert.Empty(coverage.Errors);
        Assert.Equal(2, coverage.ManifestMatched);
        Assert.Equal("sym_list", result.Old.Members.Single(m => m.PublicKey.Name == "Put"
            && m.PublicKey.Parameters[0].Type.Contains("IReadOnlyList", StringComparison.Ordinal)).ApiManifest.Single().RawSymbol);
    }

    [Fact]
    public void NullableValueTypeInTheRowIsNotTheNonNullableMember()
    {
        // The producer drops ? only for a reference-like type, so a row's int? is never the file's int.
        const string source = """
            namespace TestModule
            {
                public partial class Store
                {
                    public void Put(int value) { }
                }
            }
            """;
        var coverage = OldCoverage(source, ("Store.Put(int?)", "sym_put"));

        Assert.Equal(1, coverage.ManifestPhantom);
        Assert.Equal(0, coverage.ManifestMatched);
    }

    [Fact]
    public void OnlyStaticMethodsOfTheModuleContainerReadAsFreeFunctions()
    {
        // The root namespace is the module name, so free functions go to Functions: an instance
        // method there, or a static method of any other class, is not a free function.
        const string source = """
            namespace TestModule
            {
                public partial class Functions
                {
                    public string Helper() => "";
                }
                public static partial class Tools
                {
                    public static void Assist() { }
                }
            }
            """;
        var coverage = OldCoverage(source, ("Helper()", "sym_helper"), ("Assist()", "sym_assist"));

        Assert.Equal(2, coverage.ManifestPhantom);
        Assert.Equal(0, coverage.ManifestMatched);
    }

    [Fact]
    public void RowWithoutNullabilityIsNotANullableValueTypeMember()
    {
        // The producer keeps ? on a value type, so a row's int or struct is never the file's int?
        // or struct?: here the non-nullable overloads were deleted and only the nullable ones remain.
        const string source = """
            namespace TestModule
            {
                public struct Point { }
                public partial class Store
                {
                    public void Put(int? value) { }
                    public void Place(global::TestModule.Point? point) { }
                }
            }
            """;
        var coverage = OldCoverage(source, ("Store.Put(int)", "sym_put"), ("Store.Place(TestModule.Point)", "sym_place"));

        Assert.Equal(2, coverage.ManifestPhantom);
        Assert.Equal(0, coverage.ManifestMatched);
    }

    [Fact]
    public void OnlyTheProjectionsOwnAnyTypeStandsForATypeParameter()
    {
        const string source = """
            namespace TestModule
            {
                public partial class Store
                {
                    public void Put<T>(T value) { }
                }
            }
            """;
        var coverage = OldCoverage(source, ("Store.Put(Other.AnyType)`1", "sym_put"));

        Assert.Equal(1, coverage.ManifestPhantom);
        Assert.Equal(0, coverage.ManifestMatched);
    }

    [Fact]
    public void GenericOverloadDoesNotStandInForAMissingNonGenericOne()
    {
        // The arity relaxation runs one way: a row may keep a marker the emitter erased, but a row
        // with no marker does not describe a generic method.
        const string source = """
            namespace TestModule
            {
                public partial class Store
                {
                    public T Put<T>(int value) => default!;
                }
            }
            """;
        var coverage = OldCoverage(source, ("Store.Put(int)", "sym_put"));

        Assert.Equal(1, coverage.ManifestPhantom);
        Assert.Equal(0, coverage.ManifestMatched);
    }

    [Fact]
    public void StaticMethodOfATypeNamedLikeAContainerKeepsItsOwnRow()
    {
        // The module stutters, so its free functions are in Functions; GlobalFunctions is a Swift
        // type whose static method has its own row and is not a second free-function candidate.
        const string source = """
            namespace TestModule
            {
                public partial class Functions
                {
                    public static void Reset() { }
                }
                public partial class GlobalFunctions
                {
                    public static void Reset() { }
                }
            }
            """;
        var result = Analyze(source, ("Reset()", "sym_free"), ("GlobalFunctions.Reset()", "sym_type"));

        var coverage = Assert.Single(result.Old.Coverage);
        Assert.Empty(coverage.Errors);
        Assert.Equal(2, coverage.ManifestMatched);
        Assert.Equal("sym_free", result.Old.Members.Single(m => m.PublicKey.Name == "Reset"
            && m.PublicKey.EnclosingTypes.Single() == "Functions").ApiManifest.Single().RawSymbol);
    }
}
