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
            public sealed class AgeableAndNameableProxy : IAgeableAndNameable { public int Age => 0; public string Name => ""; }
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
    public void AKeySpellingADeclaredMembersParametersDifferently_IsListedRespelled_AndCoveredOnlyByThePendingEntry()
    {
        var report = Check(Record.Replace("\"Widget.Resize(int)\"", "\"Widget.Resize(long)\""));
        Assert.True(report.Passed);
        Assert.StartsWith("Widget.Resize(long)", Assert.Single(report.RespelledKeys));
        Assert.Equal(1, report.ByRule[SwiftDeclarationRecordCompleteness.PendingPrefix + "respelled-keys"]);
    }

    [Fact]
    public void WithoutThePendingList_TheRespelledMemberFails()
    {
        var fixedNameRules = SwiftDeclarationRecordCompleteness.Rules.Where(r => !r.IsPending).ToList();
        var report = SwiftDeclarationRecordCompleteness.Check(Image, "Kit",
            Encoding.UTF8.GetBytes(Record.Replace("\"Widget.Resize(int)\"", "\"Widget.Resize(long)\"")), fixedNameRules);
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
        Assert.Empty(report.RespelledKeys);
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
    public void ARespelledKeysPairedMember_DoesNotCoverAnotherOverloadOfItsName()
    {
        // Push(long,int) stands for Push(int,int); Push(string) is not one of its default-argument
        // overloads, so with its key gone it is uncovered.
        var report = Check(Record
            .Replace("\"Widget.Push(int,int)\"", "\"Widget.Push(long,int)\"")
            .Replace("\"Widget.Push(string)\": \"func Kit.Widget.push(name:)\",", ""));
        Assert.Equal("Widget.Push(String~)", Assert.Single(report.Uncovered).ToString());
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
    public void EveryRule_IsNamedOnce_AndJustified()
    {
        var rules = SwiftDeclarationRecordCompleteness.Rules;
        Assert.Equal(rules.Count, rules.Select(r => r.Name).Distinct().Count());
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Justification)));
        // Fixed-name rules come first; the pending list follows, so a pending entry never shadows a rule.
        Assert.Equal(rules.Count(r => !r.IsPending), rules.TakeWhile(r => !r.IsPending).Count());
    }
}
