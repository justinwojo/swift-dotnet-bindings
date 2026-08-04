// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Swift.Analyzers.Tests;

using AnalyzerTest = CSharpAnalyzerTest<SwiftStructWriteBackAnalyzer, DefaultVerifier>;

/// <summary>
/// Tests for SB1003 (<see cref="SwiftStructWriteBackAnalyzer"/>): mutating through a
/// Swift-struct-typed property or subscript of a Swift-backed object — by writing a member, or by
/// calling a Swift <c>mutating func</c> — changes only a temporary copy and is silently discarded.
/// The negative cases pin the shapes the analyzer must stay quiet on — a struct held in a
/// local/parameter/field, the copy-modify-write-back idiom that is the prescribed fix, a plain C#
/// owner, a <c>ref</c>-returning property (which hands back the storage, so the write lands), a call
/// whose result the caller consumes (indistinguishable from a read), and the deliberate
/// protocol-interface false negative.
/// </summary>
public class SwiftStructWriteBackAnalyzerTests
{
    /// <summary>
    /// Mock projection model: <c>ScanSettings</c>/<c>NestedSettings</c> stand in for non-frozen Swift
    /// structs (C# classes marked <c>ISwiftStruct</c>), <c>SessionSettings</c>/<c>Lens</c> for Swift
    /// classes (<c>ISwiftObject</c> only), and <c>PlainOwner</c> for consumer-authored C# that merely
    /// stores a wrapper instance.
    /// </summary>
    private const string MockTypes = @"
using System;

namespace Swift.Runtime
{
    public interface ISwiftObject : IDisposable
    {
    }

    public interface ISwiftStruct : ISwiftObject
    {
    }
}

public partial class ScanSettings : Swift.Runtime.ISwiftStruct
{
    public bool ReturnInputImages { get; set; }
    public int Threshold { get; set; }
    public NestedSettings Nested { get; set; }
    public int this[int index] { get { return 0; } set { } }
    public NestedSettings this[string key] { get { return Nested; } set { } }
    public void Bump(int amount) { }
    public ScanSettings Combine(int amount) { return this; }
    public string Describe() { return string.Empty; }
    public System.Threading.Tasks.Task BumpAsync(int amount)
    {
        return System.Threading.Tasks.Task.CompletedTask;
    }
    public static ScanSettings Create() { return new ScanSettings(); }
    public void Dispose() { }
}

public partial class NestedSettings : Swift.Runtime.ISwiftStruct
{
    public int Depth { get; set; }
    public void Touch() { }
    public void Dispose() { }
}

public struct FrozenRange : Swift.Runtime.ISwiftStruct
{
    public int Location { get; set; }
    public void Dispose() { }
}

public class Lens : Swift.Runtime.ISwiftObject
{
    public int Zoom { get; set; }
    public void Focus() { }
    public void Dispose() { }
}

public partial class SessionSettings : Swift.Runtime.ISwiftObject
{
    private FrozenRange _span;
    private ScanSettings _scanning = new ScanSettings();
    public ScanSettings Scanning { get; set; }
    public static ScanSettings Shared { get; set; }
    public ScanSettings Snapshot { get { return Scanning; } }
    public Lens Optics { get; set; }
    public ref FrozenRange Span { get { return ref _span; } }
    public ref ScanSettings ScanningByRef { get { return ref _scanning; } }
    public ScanSettings GetScanning() => Scanning;
    public void Dispose() { }
}

public interface IScannable
{
    ScanSettings Scanning { get; set; }
}

public class PlainOwner
{
    public ScanSettings Scanning { get; set; }
}
";

    /// <summary>
    /// The four remedy sentences SB1003 can emit. Pinned here — independently of the analyzer's
    /// own string literals — because "which advice does a consumer get" is the behaviour under
    /// test: the plain write-back recipe is actively wrong for a get-only member (it would not
    /// compile), unspellable for a subscript, and incomplete for a chain (writing back one link
    /// still mutates a copy of the link above).
    /// </summary>
    private const string WriteBackGuidance =
        "Read it into a local, mutate the local, then assign the local back: " +
        "'using var copy = ….Scanning; copy.Threshold = …; ….Scanning = copy;'.";

    private const string CallWriteBackGuidance =
        "Read it into a local, mutate the local, then assign the local back: " +
        "'using var copy = ….Scanning; copy.Bump(…); ….Scanning = copy;'.";

    private const string IndexerWriteBackGuidance =
        "Read it into a local, mutate the local, then assign the local back: " +
        "'using var copy = ….Scanning; copy[…] = …; ….Scanning = copy;'.";

    private const string NoSetterGuidance =
        "It has no setter, so there is no way to write the modified copy back; the owner cannot " +
        "be updated through it at all.";

    private const string SubscriptReceiverGuidance =
        "The element it hands back is a copy, and so is the value the subscript was read from: " +
        "assign the mutated element back through the same subscript, then assign that value back " +
        "to its own owner.";

    private const string ChainedGuidance =
        "It is itself read from a copying struct member, so every link in the chain copies: read " +
        "each link into its own local, mutate the innermost one, then assign the locals back " +
        "outward one level at a time — and if any outer link has no setter, the write cannot " +
        "reach the owner at all.";

    /// <summary>
    /// SB1003 has two descriptors — one for a lost write, one for a lost call — because the two read
    /// nothing alike in prose. They share an ID, category and severity, so <c>{|SB1003:…|}</c> markup
    /// is unambiguous about what it asserts; the option only tells the harness to stop asking which
    /// of the two to template from.
    /// </summary>
    private static AnalyzerTest NewTest(string body) => new()
    {
        TestCode = MockTypes + body,
        MarkupOptions = MarkupOptions.UseFirstDescriptor,
    };

    private static Task RunAsync(string body) => NewTest(body).RunAsync();

    /// <summary>
    /// Runs <paramref name="body"/> (marked up with <c>{|#0:…|}</c>) and asserts the full formatted
    /// SB1003 message, so the emitted guidance is verified rather than just the location.
    /// </summary>
    private static Task RunAsync(string body, string writtenName, string receiver, string guidance)
    {
        var test = NewTest(body);
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(SwiftStructWriteBackAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments(writtenName, receiver, guidance));
        return test.RunAsync();
    }

    #region Positive — the write is discarded

    [Fact]
    public async Task MemberAssignedThroughStructProperty_ReportsDiagnostic()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|SB1003:owner.Scanning.ReturnInputImages = true|};
    }
}
");
    }

    [Fact]
    public async Task MemberAssignedThroughStaticStructProperty_ReportsDiagnostic()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method()
    {
        {|SB1003:SessionSettings.Shared.Threshold = 3|};
    }
}
");
    }

    [Fact]
    public async Task NestedStructPropertyAssignedThroughStructProperty_ReportsDiagnostic()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|SB1003:owner.Scanning.Nested = new NestedSettings()|};
    }
}
");
    }

    [Fact]
    public async Task CompoundAssignmentThroughStructProperty_ReportsDiagnostic()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|SB1003:owner.Scanning.Threshold += 1|};
    }
}
");
    }

    [Fact]
    public async Task ImplicitThisStructProperty_ReportsDiagnostic()
    {
        // Generated binding types are partial, so consumer code can extend them; an unqualified
        // property reference resolves to the same copying getter as `this.Scanning`.
        await RunAsync(@"
public partial class SessionSettings
{
    public void Configure()
    {
        {|SB1003:Scanning.Threshold = 5|};
    }
}
");
    }

    [Fact]
    public async Task ParenthesizedStructPropertyReceiver_ReportsDiagnostic()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|SB1003:(owner.Scanning).Threshold = 5|};
    }
}
");
    }

    [Theory]
    [InlineData("owner.Scanning.Threshold++")]
    [InlineData("owner.Scanning.Threshold--")]
    [InlineData("++owner.Scanning.Threshold")]
    [InlineData("--owner.Scanning.Threshold")]
    public async Task IncrementOrDecrementThroughStructProperty_ReportsDiagnostic(string mutation)
    {
        // `x++` is a read-modify-write with no assignment node; it loses the write exactly as
        // `x += 1` does.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|SB1003:" + mutation + @"|};
    }
}
");
    }

    [Fact]
    public async Task IndexerAssignedThroughStructProperty_ReportsDiagnostic()
    {
        // A Swift subscript projects as a C# indexer; its setter runs on the copy just like a
        // property setter does.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|#0:owner.Scanning[0] = 5|};
    }
}
", "this[]", "the Swift struct property 'Scanning'", IndexerWriteBackGuidance);
    }

    [Fact]
    public async Task MemberAssignedThroughStructSubscript_ReportsSubscriptGuidance()
    {
        // Two copies deep: `Scanning` copies the struct, then its subscript copies the element. The
        // single-level recipe would spell the write-back as `….this[]`, which is not C#.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|#0:owner.Scanning[""a""].Depth = 7|};
    }
}
", "Depth", "the subscript on the Swift struct 'ScanSettings'", SubscriptReceiverGuidance);
    }

    [Theory]
    [InlineData("owner.Scanning!.Threshold = 5")]
    [InlineData("((ScanSettings)owner.Scanning).Threshold = 5")]
    public async Task WrappedStructPropertyReceiver_ReportsDiagnostic(string mutation)
    {
        // A null-forgiving `!` or a cast changes nothing about which storage the receiver names,
        // so neither may hide the copy.
        await RunAsync(@"
#nullable enable
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|SB1003:" + mutation + @"|};
    }
}
");
    }

    #endregion

    #region Positive — the remedy has to match the shape

    [Fact]
    public async Task SingleLevelCopy_SuggestsWriteBack()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|#0:owner.Scanning.Threshold = 5|};
    }
}
", "Threshold", "the Swift struct property 'Scanning'", WriteBackGuidance);
    }

    [Fact]
    public async Task GetOnlyStructProperty_DoesNotSuggestAnImpossibleWriteBack()
    {
        // Suggesting `owner.Snapshot = copy` here would be advice that does not compile (CS0200).
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|#0:owner.Snapshot.Threshold = 5|};
    }
}
", "Threshold", "the Swift struct property 'Snapshot'", NoSetterGuidance);
    }

    [Fact]
    public async Task ChainedCopies_SayEveryLinkMustBeWrittenBack()
    {
        // Writing back only `Nested` would still mutate a discarded copy of `Scanning`, so the
        // single-level recipe would leave the consumer with a second silent no-op.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|#0:owner.Scanning.Nested.Depth = 7|};
    }
}
", "Depth", "the Swift struct property 'Nested'", ChainedGuidance);
    }

    #endregion

    #region Positive — a call that cannot report a value loses its mutation

    [Fact]
    public async Task VoidMethodCalledThroughStructProperty_ReportsDiagnostic()
    {
        // A Swift `mutating func` projects as an ordinary C# method; the copy it mutates is the
        // one the getter just handed back, so the mutation dies with the statement.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|#0:owner.Scanning.Bump(1)|};
    }
}
", "Bump", "the Swift struct property 'Scanning'", CallWriteBackGuidance);
    }

    [Fact]
    public async Task DiscardedResultMethodCalledThroughStructProperty_ReportsDiagnostic()
    {
        // A `mutating func` that also returns a value loses its mutation just the same when the
        // caller throws the value away.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|SB1003:owner.Scanning.Combine(1)|};
    }
}
");
    }

    [Fact]
    public async Task VoidMethodCalledInsideLambda_ReportsDiagnostic()
    {
        // A void call has no value to consume wherever it appears, so statement position is not
        // required for the loss to be certain.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        System.Action a = () => {|SB1003:owner.Scanning.Bump(1)|};
        a();
    }
}
");
    }

    [Fact]
    public async Task ConditionallyCalledMethodThroughStructProperty_ReportsDiagnostic()
    {
        // `?.` is the natural spelling once a struct property is optional-typed, and it is the one
        // receiver shape a call can have that an assignment cannot. The copy is lost all the same.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|#0:owner.Scanning?.Bump(1)|};
    }
}
", "Bump", "the Swift struct property 'Scanning'", CallWriteBackGuidance);
    }

    [Fact]
    public async Task AwaitedMethodCalledThroughStructProperty_ReportsDiagnostic()
    {
        // An awaited call in statement position discards its result exactly as a plain one does;
        // the `await` in between must not hide that.
        await RunAsync(@"
public class TestClass
{
    public async System.Threading.Tasks.Task Method(SessionSettings owner)
    {
        await {|SB1003:owner.Scanning.BumpAsync(1)|};
    }
}
");
    }

    [Fact]
    public async Task VoidMethodCalledThroughNestedStructProperty_ReportsChainedGuidance()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|#0:owner.Scanning.Nested.Touch()|};
    }
}
", "Touch", "the Swift struct property 'Nested'", ChainedGuidance);
    }

    [Fact]
    public async Task VoidMethodCalledThroughGetOnlyStructProperty_ReportsNoSetterGuidance()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|#0:owner.Snapshot.Bump(1)|};
    }
}
", "Bump", "the Swift struct property 'Snapshot'", NoSetterGuidance);
    }

    [Fact]
    public async Task VoidMethodCalledThroughStructSubscript_ReportsSubscriptGuidance()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        {|#0:owner.Scanning[""a""].Touch()|};
    }
}
", "Touch", "the subscript on the Swift struct 'ScanSettings'", SubscriptReceiverGuidance);
    }

    #endregion

    #region Negative — the call keeps its meaning, or the write reaches the owner

    [Fact]
    public async Task MethodResultConsumed_NoDiagnostic()
    {
        // A call whose value the caller uses is a read, and a read off a copy returns the same
        // answer the owner would. Nothing in the projected C# marks a method `mutating`, so this
        // is where the analyzer stops rather than guess.
        await RunAsync(@"
public class TestClass
{
    public int Method(SessionSettings owner)
    {
        var described = owner.Scanning.Describe();
        var pending = owner.Scanning.BumpAsync(1);
        return described.Length + owner.Scanning.Combine(1).Threshold + pending.Id;
    }
}
");
    }

    [Fact]
    public async Task DisposeOnStructProperty_NoDiagnostic()
    {
        // Disposing the copy is the correct thing to do with a copy — it releases the native
        // buffer the getter just allocated.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        owner.Scanning.Dispose();
    }
}
");
    }

    [Fact]
    public async Task ObjectOverrideCalledOnStructProperty_NoDiagnostic()
    {
        // Generated struct projections override the System.Object members; none of them mutates.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        owner.Scanning.ToString();
        owner.Scanning.GetHashCode();
    }
}
");
    }

    [Fact]
    public async Task StaticMethodOnStructProjection_NoDiagnostic()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method()
    {
        ScanSettings.Create();
    }
}
");
    }

    [Fact]
    public async Task MethodCalledOnSwiftClassProperty_NoDiagnostic()
    {
        // Reference semantics: the getter hands back the same instance, so the call reaches it.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        owner.Optics.Focus();
    }
}
");
    }

    [Fact]
    public async Task MethodCalledOnLocalCopy_NoDiagnostic()
    {
        // The write-back idiom itself: the local is the copy, and it is assigned back.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        using var copy = owner.Scanning;
        copy.Bump(1);
        owner.Scanning = copy;
    }
}
");
    }

    [Fact]
    public async Task MethodCalledOnPlainCSharpOwnersProperty_NoDiagnostic()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method(PlainOwner owner)
    {
        owner.Scanning.Bump(1);
    }
}
");
    }

    #endregion

    #region Negative — a ref-returning property is not a copy

    [Fact]
    public async Task WriteThroughRefReturningStructProperty_NoDiagnostic()
    {
        // `ref` returns the storage itself, so the write lands. The generator never emits a
        // ref-returning property, so this shape is always consumer-authored.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        owner.Span.Location = 1;
        owner.ScanningByRef.Threshold = 5;
        owner.ScanningByRef.Bump(1);
    }
}
");
    }

    #endregion

    #region Negative — the write reaches the owner

    [Fact]
    public async Task StructHeldInLocal_NoDiagnostic()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        var copy = owner.Scanning;
        copy.ReturnInputImages = true;
    }
}
");
    }

    [Fact]
    public async Task CopyModifyWriteBackIdiom_NoDiagnostic()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        using var copy = owner.Scanning;
        copy.ReturnInputImages = true;
        copy.Threshold = 2;
        owner.Scanning = copy;
    }
}
");
    }

    [Fact]
    public async Task StructHeldInField_NoDiagnostic()
    {
        await RunAsync(@"
public class TestClass
{
    private ScanSettings _settings = new ScanSettings();

    public void Method()
    {
        _settings.ReturnInputImages = true;
        this._settings.Threshold = 4;
    }
}
");
    }

    [Fact]
    public async Task StructHeldInParameter_NoDiagnostic()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method(ScanSettings settings)
    {
        settings.ReturnInputImages = true;
    }
}
");
    }

    [Fact]
    public async Task SwiftClassPropertyReceiver_NoDiagnostic()
    {
        // A Swift class projects with reference semantics — the getter hands back the same instance,
        // so mutating through it is observed by the owner.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        owner.Optics.Zoom = 3;
    }
}
");
    }

    [Fact]
    public async Task PlainCSharpOwnerOfStructProjection_NoDiagnostic()
    {
        // A consumer-authored property is not a copying binding getter; it stores and returns the
        // same wrapper instance.
        await RunAsync(@"
public class TestClass
{
    public void Method(PlainOwner owner)
    {
        owner.Scanning.ReturnInputImages = true;
    }
}
");
    }

    [Fact]
    public async Task WholeValueAssignmentToStructProperty_NoDiagnostic()
    {
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner, ScanSettings value)
    {
        owner.Scanning = value;
        SessionSettings.Shared = value;
    }
}
");
    }

    [Fact]
    public async Task MethodCallReceiver_NoDiagnostic()
    {
        // Documented limitation: only a property receiver is known to copy. A method result is out
        // of scope rather than assumed.
        await RunAsync(@"
public class TestClass
{
    public void Method(SessionSettings owner)
    {
        owner.GetScanning().ReturnInputImages = true;
    }
}
");
    }

    [Fact]
    public async Task IndexerOnLocalOrFieldStruct_NoDiagnostic()
    {
        await RunAsync(@"
public class TestClass
{
    private ScanSettings _settings = new ScanSettings();

    public void Method(SessionSettings owner)
    {
        var copy = owner.Scanning;
        copy[0] = 5;
        _settings[1] = 6;
        copy.Threshold++;
    }
}
");
    }

    [Fact]
    public async Task StructPropertyOnProtocolInterface_NoDiagnostic()
    {
        // Documented limitation. Generated protocol interfaces do not implement ISwiftObject, and
        // they are explicitly consumer-implementable — a consumer's own implementation may return a
        // stored wrapper, where mutating through it is correct. Accepting any interface receiver
        // would swap this false negative for a false positive on consumer code, so the analyzer
        // stays quiet here on purpose.
        await RunAsync(@"
public class TestClass
{
    public void Method(IScannable owner)
    {
        owner.Scanning.Threshold = 5;
    }
}
");
    }

    [Fact]
    public async Task NonSwiftTypes_NoDiagnostic()
    {
        await RunAsync(@"
public class Inner
{
    public int Value { get; set; }
}

public class Outer
{
    public Inner Child { get; set; }
}

public class TestClass
{
    public void Method(Outer outer)
    {
        outer.Child.Value = 1;
    }
}
");
    }

    #endregion
}
