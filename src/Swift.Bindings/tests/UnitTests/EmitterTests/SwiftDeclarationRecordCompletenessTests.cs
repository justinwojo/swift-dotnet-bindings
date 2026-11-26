// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using System.Text;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The compile gate's completeness check: every public type and member of a module is named by the
/// module's Swift declaration record or by a justified fixed-name rule, and every record key names a
/// member the assembly declares.
/// </summary>
public class SwiftDeclarationRecordCompletenessTests
{
    private const string Api = """
        namespace Kit
        {
            public sealed class Widget : System.IDisposable
            {
                public Widget(int size) { }
                public int Size => 0;
                public void Resize(int size) { }
                public void Move(int x) { }
                public void Move(string x) { }
                public void Take(Other.A.Token t) { }
                public void Take(Other.B.Token t) { }
                public static int Answer() => 1;
                public int Handle => 0;
                public void Push(int a, int b) { }
                public void Push(string s) { }
                public void Dispose() { }
                public override string ToString() => "";
                public static bool operator ==(Widget a, Widget b) => true;
                public static bool operator !=(Widget a, Widget b) => false;
                public override bool Equals(object? o) => false;
                public override int GetHashCode() => 0;
            }
            public enum Mode { Fast = 1, Slow = 2 }
            public sealed class Shape
            {
                private Shape() { }
                public enum CaseTag { Circle = 1, Square = 2 }
                public static Shape Circle(double radius) => new();
                public bool TryGetCircle(out double radius) { radius = 0; return false; }
                public static Shape Square => new();
            }
            public partial class Functions { public static int Answer() => 42; public static void MarshalToSwift(int x) { } }
            public sealed class GlobalFunctions : System.IDisposable { public GlobalFunctions() { } public void Dispose() { } }
            public static class WidgetExtensions { public static int Doubled(this Widget w) => 0; }
            public interface IAgeable { int Age { get; } }
            public interface INameable { string Name { get; } }
            public interface IAgeableAndNameable : IAgeable, INameable { }
            // A composition proxy is constructed from an existential container (runtime plumbing), not by a public default constructor.
            public sealed class AgeableAndNameableProxy : IAgeableAndNameable { private AgeableAndNameableProxy() { } public int Age => 0; public string Name => ""; }
            public sealed class Foreign { public sealed class Nested { private Nested() { } } }
        }
        namespace Other.A { public sealed class Token { } }
        namespace Other.B { public sealed class Token { } }
        """;

    private const string Record = """
        {
          "schema_version": 1,
          "module": "Kit",
          "types": {
            "Widget": "type Kit.Widget",
            "Mode": "type Kit.Mode",
            "Shape": "type Kit.Shape",
            "IAgeable": "type Kit.Ageable",
            "INameable": "type Kit.Nameable",
            "Foreign.Nested": "type Kit.Foreign.Nested",
            "GlobalFunctions": "type Kit.GlobalFunctions"
          },
          "members": {
            "Widget.Widget(int)": "init Kit.Widget.init(size:)",
            "Widget.Size": "var Kit.Widget.size",
            "Widget.Resize(int)": "func Kit.Widget.resize(size:)",
            "Widget.Move(int)": "func Kit.Widget.move(x:)",
            "Widget.Move(string)": "func Kit.Widget.move(name:)",
            "Widget.Take(A.Token)": "func Kit.Widget.take(_:)",
            "Widget.Take(B.Token)": "func Kit.Widget.take(other:)",
            "Widget.Answer()": "static func Kit.Widget.answer()",
            "Widget.Handle": "var Kit.Widget.handle",
            "Widget.Push(int,int)": "func Kit.Widget.push(a:b:)",
            "Widget.Push(string)": "func Kit.Widget.push(name:)",
            "MarshalToSwift(int)": "func Kit.marshalToSwift(_:)",
            "GlobalFunctions.GlobalFunctions()": "init Kit.GlobalFunctions.init()",
            "Answer()": "func Kit.answer()",
            "WidgetExtensions.Doubled(this Widget)": "func Kit.Widget.doubled() (extension in Kit)",
            "IAgeable.Age": "var Kit.Ageable.age",
            "INameable.Name": "var Kit.Nameable.name"
          }
        }
        """;

    private static readonly byte[] Image = SwiftTransitionManifestTests.Compile("Kit", Api);

    private static SwiftDeclarationRecordCompleteness.Report Check(string record) =>
        SwiftDeclarationRecordCompleteness.Check(Image, "Kit", Encoding.UTF8.GetBytes(record));

    [Fact]
    public void ARecordedModule_Passes_WithTheFixedNamesItDoesNotRecordMatchedByRules()
    {
        var report = Check(Record);
        Assert.True(report.Passed, string.Join("; ", report.Uncovered.Select(m => m.ToString()).Concat(report.UnresolvedKeys)));
        Assert.True(report.ByRule["lifecycle-and-equality"] > 0);
        Assert.True(report.ByRule["operators"] > 0);
        Assert.True(report.ByRule["case-constants"] > 0);
        Assert.True(report.ByRule["payload-case-members"] >= 3);
        Assert.True(report.ByRule["module-function-holder"] > 0);
        Assert.True(report.ByRule["generated-containers"] > 0);
        Assert.True(report.ByRule["protocol-compositions"] >= 3);
        Assert.True(report.ByRule["foreign-type-hosts"] >= 2);
    }

    [Fact]
    public void AMemberNeitherRecordedNorMatched_Fails_NamingIt()
    {
        // A newly emitted kind: Resize loses its record entry, and no rule names it.
        var report = Check(Record.Replace("\"Widget.Resize(int)\": \"func Kit.Widget.resize(size:)\",", ""));
        Assert.False(report.Passed);
        Assert.Equal("Widget.Resize(Int32)", Assert.Single(report.Uncovered).ToString());
    }

    [Fact]
    public void ATypeNeitherRecordedNorMatched_Fails_NamingIt()
    {
        var report = Check(Record.Replace("\"Mode\": \"type Kit.Mode\",", ""));
        Assert.False(report.Passed);
        Assert.Equal("Mode", Assert.Single(report.Uncovered).ToString());
    }

    [Fact]
    public void AKeyNamingNothing_Fails_AsUnresolved()
    {
        var report = Check(Record.Replace("\"Answer()\"", "\"Answer()\": \"func Kit.answer()\", \"Widget.Missing(int)\""));
        Assert.False(report.Passed);
        Assert.StartsWith("Widget.Missing(int)", Assert.Single(report.UnresolvedKeys));
    }

    [Fact]
    public void AKeySpellingADeclaredMembersParametersDifferently_IsUnresolved_AndTheMemberUncovered()
    {
        // A key builder that spells a parameter as its Swift type (long for the member's int) names a
        // signature the binding does not have: the key fails, and the member it means is unchecked.
        var report = Check(Record.Replace("\"Widget.Resize(int)\"", "\"Widget.Resize(long)\""));
        Assert.False(report.Passed);
        Assert.StartsWith("Widget.Resize(long)", Assert.Single(report.UnresolvedKeys));
        Assert.Equal("Widget.Resize(Int32)", Assert.Single(report.Uncovered).ToString());
    }

    [Fact]
    public void ARespelledKey_StandsForOneUnrecordedOverload_NotEveryOverloadAtItsPath()
    {
        // Both Move overloads lose their keys and one respelled key is left: it cannot say which
        // overload it means, so it is unresolved and both overloads are uncovered.
        var report = Check(Record
            .Replace("\"Widget.Move(int)\": \"func Kit.Widget.move(x:)\",", "")
            .Replace("\"Widget.Move(string)\"", "\"Widget.Move(long)\""));
        Assert.False(report.Passed);
        Assert.StartsWith("Widget.Move(long)", Assert.Single(report.UnresolvedKeys));
        Assert.Equal(new[] { "Widget.Move(Int32)", "Widget.Move(String~)" }, report.Uncovered.Select(m => m.ToString()).Order());
    }

    [Fact]
    public void ARespelledKey_BesideRecordedOverloadsOnly_IsUnresolved()
    {
        var report = Check(Record.Replace("\"Widget.Handle\"", "\"Widget.Move(long)\": \"func Kit.Widget.move(count:)\", \"Widget.Handle\""));
        Assert.False(report.Passed);
        Assert.StartsWith("Widget.Move(long)", Assert.Single(report.UnresolvedKeys));
    }

    [Fact]
    public void OneKey_CoversOneMember_WhenItSpellsTwo()
    {
        // A key does not spell a parameter's namespace, so Take(A.Token) and Take(B.Token) both name
        // both overloads; each key still covers only one of them.
        var report = Check(Record.Replace("\"Widget.Take(B.Token)\": \"func Kit.Widget.take(other:)\",", ""));
        Assert.False(report.Passed);
        Assert.Equal("Widget.Take", Assert.Single(report.Uncovered).Path);
    }

    [Fact]
    public void AModuleFunctionKey_NamesOnlyTheFunctionsHoldersMember()
    {
        var report = Check(Record.Replace("\"Widget.Answer()\": \"static func Kit.Widget.answer()\",", ""));
        Assert.False(report.Passed);
        Assert.Equal("Widget.Answer()", Assert.Single(report.Uncovered).ToString());
    }

    [Fact]
    public void ASwiftPropertyNamedLikeRuntimePlumbing_IsNotCoveredByThePlumbingRule()
    {
        // Widget implements no runtime interface, so its Int32 Handle is a Swift property.
        var report = Check(Record.Replace("\"Widget.Handle\": \"var Kit.Widget.handle\",", ""));
        Assert.False(report.Passed);
        Assert.Equal("Widget.Handle", Assert.Single(report.Uncovered).ToString());
    }

    [Fact]
    public void ASwiftClassNamedLikeTheFunctionHolder_IsNotTakenForIt()
    {
        var report = Check(Record
            .Replace("\"GlobalFunctions\": \"type Kit.GlobalFunctions\"", "\"Mode\": \"type Kit.Mode\"")
            .Replace("\"Mode\": \"type Kit.Mode\",", "")
            .Replace("\"GlobalFunctions.GlobalFunctions()\": \"init Kit.GlobalFunctions.init()\",", ""));
        Assert.Equal(new[] { "GlobalFunctions", "GlobalFunctions..ctor()" }, report.Uncovered.Select(m => m.ToString()).Order());
    }

    [Fact]
    public void AModuleFunctionNamedLikeRuntimePlumbing_IsNotCoveredByThePlumbingRule()
    {
        var report = Check(Record.Replace("\"MarshalToSwift(int)\": \"func Kit.marshalToSwift(_:)\",", ""));
        Assert.Equal("Functions.MarshalToSwift(Int32)", Assert.Single(report.Uncovered).ToString());
    }

    [Fact]
    public void EveryRule_IsNamedOnce_Justified_AndFixedName()
    {
        var rules = SwiftDeclarationRecordCompleteness.Rules;
        Assert.Equal(rules.Count, rules.Select(r => r.Name).Distinct().Count());
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Justification)));
        // Every emitted member is recorded at its emitter or matched by a fixed-name rule: no rule
        // stands in for members the record should describe.
        Assert.DoesNotContain(rules, r => r.Name.StartsWith("pending:", StringComparison.Ordinal));
    }

    [Fact]
    public void AProxy_IsCheckedAgainstTheInterfaceItImplements_NotANestedInterfaceOfItsName()
    {
        // AHost.IListener comes first in metadata; ListenerProxy implements the top-level IListener.
        var image = SwiftTransitionManifestTests.Compile("Kit", """
            namespace Kit
            {
                public sealed class AHost { private AHost() { } public interface IListener { void Heard(); } }
                public interface IListener { void Spoke(); }
            }
            namespace Kit.SwiftInterop
            {
                public sealed class ListenerProxy : Kit.IListener
                {
                    public ListenerProxy(Kit.IListener impl) { }
                    public void Spoke() { }
                }
            }
            """);
        var report = SwiftDeclarationRecordCompleteness.Check(image, "Kit", Encoding.UTF8.GetBytes("""
            {
              "schema_version": 1, "module": "Kit",
              "types": { "AHost": "type Kit.AHost", "AHost.IListener": "type Kit.AHost.Listener", "IListener": "type Kit.Listener" },
              "members": { "AHost.IListener.Heard()": "func Kit.AHost.Listener.heard()", "IListener.Spoke()": "func Kit.Listener.spoke()" }
            }
            """));
        Assert.True(report.Passed, string.Join("; ", report.Uncovered.Select(m => m.ToString()).Concat(report.UnresolvedKeys)));
        Assert.True(report.ByRule["protocol-proxies"] >= 3);
    }

    [Fact]
    public void ASwiftMethodNamedLikeAnISwiftObjectMember_IsNotCoveredByThePlumbingRule()
    {
        // Codec implements a runtime interface; only the members of the runtime's signatures are plumbing.
        var image = SwiftTransitionManifestTests.Compile("Kit", """
            namespace Swift.Runtime { public interface ISwiftObject { } public struct TypeMetadata { } }
            namespace Kit
            {
                public sealed class Codec : Swift.Runtime.ISwiftObject
                {
                    private Codec() { }
                    public static Swift.Runtime.TypeMetadata GetTypeMetadata() => default;
                    public int MarshalToSwift(ref System.Span<byte> destination) => 0;
                    public static void MarshalToSwift(int value) { }
                    public static int NewFromPayload(System.IntPtr payload) => 0;
                }
            }
            """);
        var report = SwiftDeclarationRecordCompleteness.Check(image, "Kit", Encoding.UTF8.GetBytes("""
            { "schema_version": 1, "module": "Kit", "types": { "Codec": "type Kit.Codec" }, "members": { } }
            """));
        Assert.Equal(new[] { "Codec.MarshalToSwift(Int32)", "Codec.NewFromPayload(IntPtr)" }, report.Uncovered.Select(m => m.ToString()).Order());
    }

    [Fact]
    public void ACaselessSwiftEnumNamedLikeTheFunctionHolder_WithoutItsTypeKey_IsUncovered()
    {
        // A caseless Swift enum emits as a static class; the generated holder is a non-static one.
        var image = SwiftTransitionManifestTests.Compile("Kit", """
            namespace Kit { public static partial class Functions { public static void Ping() { } } }
            """);
        var report = SwiftDeclarationRecordCompleteness.Check(image, "Kit", Encoding.UTF8.GetBytes("""
            { "schema_version": 1, "module": "Kit", "types": { }, "members": { "Functions.Ping()": "static func Kit.Functions.ping()" } }
            """));
        Assert.Equal("Functions", Assert.Single(report.Uncovered).ToString());
    }

    // A proxy whose interface refines another binding's interface (Dep.IAnchored), and a composition
    // proxy: each implements its interfaces' requirements, and either may carry a member none declares.
    private const string ForeignProxyApi = """
        namespace Dep { public interface IAnchored { int Anchor { get; } } }
        namespace Kit
        {
            public interface IListener : Dep.IAnchored { void Spoke(); }
            public interface IAgeable { int Age { get; } }
            public interface INameable { string Name { get; } }
            public interface IAgeableAndNameable : IAgeable, INameable { }
            public sealed class AgeableAndNameableProxy : IAgeableAndNameable { private AgeableAndNameableProxy() { } public int Age => 0; public string Name => ""; public void Extra() { } }
        }
        namespace Kit.SwiftInterop
        {
            public sealed class ListenerProxy : Kit.IListener
            {
                public ListenerProxy(Kit.IListener impl) { }
                public void Spoke() { }
                public int Anchor => 0;
                public void Extra() { }
            }
        }
        """;

    private const string ForeignProxyRecord = """
        {
          "schema_version": 1, "module": "Kit",
          "types": { "IListener": "type Kit.Listener", "IAgeable": "type Kit.Ageable", "INameable": "type Kit.Nameable" },
          "members": { "IListener.Spoke()": "func Kit.Listener.spoke()", "IAgeable.Age": "var Kit.Ageable.age", "INameable.Name": "var Kit.Nameable.name" }
        }
        """;

    [Fact]
    public void AProxyMember_NoInterfaceOfItsChainDeclares_IsUncovered_EvenBesideAForeignInterface()
    {
        // ListenerProxy.Anchor implements the dependency's requirement, which the assembly declares;
        // ListenerProxy.Extra and the composition proxy's Extra implement nothing.
        var image = SwiftTransitionManifestTests.Compile("Kit", ForeignProxyApi);
        var report = SwiftDeclarationRecordCompleteness.Check(image, "Kit", Encoding.UTF8.GetBytes(ForeignProxyRecord));
        Assert.Equal(new[] { "AgeableAndNameableProxy.Extra()", "SwiftInterop.ListenerProxy.Extra()" }, report.Uncovered.Select(m => m.ToString()).Order());
    }

    [Fact]
    public void AProxyMember_OfAForeignInterfaceTheAssemblyDoesNotDeclare_IsUncovered()
    {
        // The dependency's interface lives in another assembly the check cannot read: nothing proves
        // the member implements it, so it fails closed rather than being taken on trust.
        var image = SwiftTransitionManifestTests.Compile("Kit", ForeignProxyApi.Replace("namespace Dep { public interface IAnchored { int Anchor { get; } } }",
            "namespace Dep { public interface IAnchored { } }"));
        var report = SwiftDeclarationRecordCompleteness.Check(image, "Kit", Encoding.UTF8.GetBytes(ForeignProxyRecord));
        Assert.Contains("SwiftInterop.ListenerProxy.Anchor", report.Uncovered.Select(m => m.ToString()));
    }

    [Fact]
    public void ATypeKeyNamingNoDeclaredType_Fails_AsUnresolved()
    {
        // A foreign receiver's type key left in the record for a class the module never declares fails
        // the check rather than passing: it names nothing the merge could compare.
        var report = Check(Record.Replace("\"Mode\": \"type Kit.Mode\",", "\"Mode\": \"type Kit.Mode\", \"Receiver\": \"type Dep.Receiver\","));
        Assert.False(report.Passed);
        Assert.StartsWith("type Receiver", Assert.Single(report.UnresolvedKeys));
    }
}
