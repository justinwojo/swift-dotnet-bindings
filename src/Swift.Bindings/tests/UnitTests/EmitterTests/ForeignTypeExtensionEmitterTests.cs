// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ForeignTypeExtensionEmitter — C# static extension classes for Swift extensions
/// on foreign types (types not defined in the current module, e.g., UIKit.UIView).
/// </summary>
public class ForeignTypeExtensionEmitterTests
{
    private static readonly Microsoft.Extensions.Logging.ILogger Logger = NullLogger.Instance;

    #region ProcessForeignTypeExtensions: empty input

    [Fact]
    public void ProcessForeignTypeExtensions_EmptyDict_NoOutput()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, new Dictionary<string, List<ProtocolExtensionMethodDecl>>(),
            typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: non-ObjC type skipped

    [Fact]
    public void ProcessForeignTypeExtensions_NonObjCType_Skipped()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        // SwiftModule.Int is not an ObjC class — should be skipped
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["Swift.Int"] = new()
            {
                CreateExtMethod("doubled", "public func doubled() -> Swift.Int")
            }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: constrained extension skipped

    [Fact]
    public void ProcessForeignTypeExtensions_ConstrainedExtension_Skipped()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("customMethod", "public func customMethod() -> Swift.Int");
        method.WhereConstraints = new List<string> { "Element : Comparable" };

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: deprecated member skipped

    [Fact]
    public void ProcessForeignTypeExtensions_DeprecatedMember_Skipped()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("oldMethod", "public func oldMethod()");
        method.IsDeprecated = true;

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: static member skipped

    [Fact]
    public void ProcessForeignTypeExtensions_StaticMember_Skipped()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("shared", "public static func shared()");
        method.IsStatic = true;

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region Declined members are reported, and survive the containment session restart

    /// <summary>
    /// Runs the pre-pass the way generation does: outside containment, whose fresh report session then
    /// discards everything the pre-pass wrote, so only <see cref="ForeignTypeExtensionEmitter.RepublishWithdrawals"/>
    /// can carry a row into the report that is written.
    /// </summary>
    private static BindingReport RunPrePassAcrossASessionRestart(
        ModuleDecl moduleDecl, Dictionary<string, List<ProtocolExtensionMethodDecl>> extensions, ModuleEmissionContext ctx)
    {
        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(moduleDecl, extensions, CreateTypeDatabase(), Logger, ctx);
        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.RepublishWithdrawals(moduleDecl, ctx);
        var report = ReportCollector.Complete()!;
        ReportCollector.Reset();
        return report;
    }

    public static TheoryData<string, SkipReason> DeclinedShapes => new()
    {
        { "constrained", SkipReason.ConstrainedExtensionWrapper },
        { "deprecated", SkipReason.UnsupportedSignature },
        { "static", SkipReason.UnsupportedSignature },
        { "async", SkipReason.UnsupportedSignature },
        { "throwing", SkipReason.UnsupportedSignature },
    };

    [Theory]
    [MemberData(nameof(DeclinedShapes))]
    public void ProcessForeignTypeExtensions_DeclinedShape_ReportedAgainstTheReceiver(string shape, SkipReason expected)
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var method = shape switch
        {
            "async" => CreateExtMethod("refresh", "public func refresh() async"),
            "throwing" => CreateExtMethod("refresh", "public func refresh() throws"),
            _ => CreateExtMethod("refresh", "public func refresh()"),
        };
        if (shape == "constrained") method.WhereConstraints = new List<string> { "Element : Comparable" };
        if (shape == "deprecated") method.IsDeprecated = true;
        if (shape == "static") method.IsStatic = true;

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["UIKit.UIView"] = new() { method } }, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
        var row = Assert.Single(report.SkippedItems);
        Assert.Equal("refresh", row.Name);
        Assert.Equal("UIKit.UIView", row.ContainingType);
        Assert.Equal(expected, row.Reason);
        Assert.Contains("UIKit.UIView", row.Details);
    }

    /// <summary>
    /// A signature the per-kind binders cannot marshal (here a generic method) is reported against the
    /// member, whichever of their many internal gates declined it.
    /// </summary>
    [Fact]
    public void ProcessForeignTypeExtensions_UnmarshallableSignature_Reported()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var method = CreateExtMethod("convert", "public func convert<T>(_ value: T) -> Swift.Int");

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["UIKit.UIView"] = new() { method } }, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
        var row = Assert.Single(report.SkippedItems);
        Assert.Equal("convert", row.Name);
        Assert.Equal("UIKit.UIView", row.ContainingType);
        Assert.Equal(SkipReason.UnsupportedSignature, row.Reason);
    }

    /// <summary>
    /// A member listed twice binds once through its first claim; the second listing is the same member,
    /// already bound, so it is not reported as skipped.
    /// </summary>
    [Fact]
    public void ProcessForeignTypeExtensions_SameMemberListedTwice_BoundOnceAndNotReported()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl,
            new()
            {
                ["UIKit.UIView"] = new()
                {
                    CreateExtMethod("refresh", "public func refresh()"),
                    CreateExtMethod("refresh", "public func refresh()"),
                },
            },
            ctx);

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
        Assert.Empty(report.SkippedItems);
    }

    /// <summary>
    /// Two different members whose wrappers render the same symbol cannot both bind; the one that
    /// loses the claim is reported against the receiver rather than taken for a duplicate listing.
    /// </summary>
    [Fact]
    public void ProcessForeignTypeExtensions_DifferentMembersSharingAWrapperSymbol_LoserReported()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var property = CreateExtMethod("rank", "public var rank: Swift.Int32 { get }");
        property.IsProperty = true;
        var method = CreateExtMethod("get_rank", "public func get_rank() -> Swift.Int32");

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["UIKit.UIView"] = new() { property, method } }, ctx);

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
        var row = Assert.Single(report.SkippedItems);
        Assert.Equal("get_rank", row.Name);
        Assert.Equal("UIKit.UIView", row.ContainingType);
        Assert.Equal(SkipReason.EmitterFault, row.Reason);
        Assert.Contains("SBSW_UIKit_UIView_get_rank", row.Details);
    }

    /// <summary>
    /// A settable property whose value this path marshals only out of a getter keeps its getter, and
    /// its setter is reported rather than dropped.
    /// </summary>
    [Fact]
    public void ProcessForeignTypeExtensions_SetterOfANonPrimitiveValue_ReportedAndGetterBound()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var property = CreateExtMethod("snapshotView", "public var snapshotView: UIKit.UIView { get set }");
        property.IsProperty = true;
        property.HasSetter = true;

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["UIKit.UIView"] = new() { property } }, ctx);

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
        var row = Assert.Single(report.SkippedItems);
        Assert.Equal("snapshotView", row.Name);
        Assert.Equal(SkipReason.UnsupportedSignature, row.Reason);
        Assert.Equal(AccessorKind.Setter, DeclId.Parse(row.DeclId!).Accessor);
    }

    /// <summary>
    /// A member a module adds to a foreign Swift enum or struct is not bound by this path. It is
    /// reported, against the receiver it extends, rather than dropped without a trace.
    /// </summary>
    [Theory]
    [InlineData("Enum")]
    [InlineData("Struct")]
    public void ProcessForeignTypeExtensions_MemberOfForeignSwiftValueType_Reported(string declaredKind)
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.ForeignTypeDeclKinds["Foundation.AttributeScopes"] = declaredKind;
        var property = CreateExtMethod("translation", "public var translation: Swift.Int { get }");
        property.IsProperty = true;
        var method = CreateExtMethod("describe", "public func describe() -> Swift.Int");

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["Foundation.AttributeScopes"] = new() { property, method } }, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
        Assert.Equal(
            new[] { "describe|Method", "translation|Property" },
            report.SkippedItems.Select(r => $"{r.Name}|{r.Kind}").OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(report.SkippedItems, r =>
        {
            Assert.Equal("Foundation.AttributeScopes", r.ContainingType);
            Assert.Equal(SkipReason.UnsupportedSignature, r.Reason);
            Assert.Contains(declaredKind.ToLowerInvariant(), r.Details);
        });
    }

    /// <summary>
    /// A frozen-struct or class receiver the parser kept is bound by the cross-module extension path,
    /// which binds or reports each member the ABI lists. A member the ABI lists is left to it; one
    /// the interface declares and the ABI does not is reported once both paths ran.
    /// </summary>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void ProcessForeignTypeExtensions_ReceiverCarriedByTheCrossModulePath_ReportedOnlyWhenTheAbiDoesNotList(
        bool abiLists, int expectedRows)
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.ForeignTypeDeclKinds["Foundation.Measurement"] = "Struct";
        moduleDecl.Types.Add(new StructDecl
        {
            Name = "Measurement",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Measurement"),
            MangledName = "$s10Foundation11MeasurementV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "",
        });
        if (abiLists)
            moduleDecl.Types[^1].Properties.Add(AbiProperty("doubled", moduleDecl));
        var property = CreateExtMethod("doubled", "public var doubled: Swift.Int { get }");
        property.IsProperty = true;

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["Foundation.Measurement"] = new() { property } }, ctx);

        Assert.Equal(expectedRows, report.SkippedItems.Count);
        if (expectedRows > 0)
        {
            var row = Assert.Single(report.SkippedItems);
            Assert.Equal("doubled", row.Name);
            Assert.Equal("Foundation.Measurement", row.ContainingType);
            Assert.Contains("does not list", row.Details);
        }
    }

    /// <summary>
    /// A foreign protocol the parser kept for its extension members (<c>extension Combine.Cancellable</c>)
    /// is not a receiver the cross-module path carries: that path binds classes and structs only. Its
    /// members are reported here rather than left to a path that drops them without a row.
    /// </summary>
    [Fact]
    public void ProcessForeignTypeExtensions_MemberOfAKeptForeignProtocol_Reported()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.ForeignTypeDeclKinds["Combine.Cancellable"] = "Protocol";
        moduleDecl.Types.Add(new ProtocolDecl
        {
            Name = "Cancellable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Combine.Cancellable"),
            MangledName = "$s7Combine11CancellableP",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        });
        var method = CreateExtMethod("storeWhileEntityActive", "public func storeWhileEntityActive(_ entity: TestModule.Anchor)");

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["Combine.Cancellable"] = new() { method } }, ctx);

        var row = Assert.Single(report.SkippedItems);
        Assert.Equal("storeWhileEntityActive", row.Name);
        Assert.Equal("Combine.Cancellable", row.ContainingType);
        Assert.Equal(SkipReason.UnsupportedSignature, row.Reason);
        Assert.Contains("protocol", row.Details);
    }

    /// <summary>
    /// On a kept foreign protocol whose interface the run can read, a member supplying the default of
    /// the protocol's own requirement (<c>cancel()</c>) is reached through that requirement and has no
    /// row; an addition, and an overload with other labels, keep theirs.
    /// </summary>
    [Fact]
    public void ProcessForeignTypeExtensions_DefaultOfAKeptForeignProtocolsRequirement_HasNoRow()
    {
        using var sdk = FakeSdkInterfaces.Create(("Combine", true, """
            public protocol Cancellable {
              func cancel()
            }
            """));
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.ForeignTypeDeclKinds["Combine.Cancellable"] = "Protocol";
        moduleDecl.Types.Add(new ProtocolDecl
        {
            Name = "Cancellable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Combine.Cancellable"),
            MangledName = "$s7Combine11CancellableP",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        });
        var members = new List<ProtocolExtensionMethodDecl>
        {
            CreateExtMethod("cancel", "public func cancel()"),
            CreateLabeledExtMethod("cancel", "cancel(after:)", "public func cancel(after delay: Swift.Int)"),
            CreateExtMethod("storeWhileEntityActive", "public func storeWhileEntityActive(_ entity: TestModule.Anchor)"),
        };

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["Combine.Cancellable"] = members }, ctx);

        Assert.Equal(2, report.SkippedItems.Count);
        Assert.Single(report.SkippedItems, r => r.Name == "storeWhileEntityActive");
        Assert.Single(report.SkippedItems, r => r.Name == "cancel");
    }

    /// <summary>
    /// A declined member of a class receiver the parser kept (an Objective-C-module Swift class) is left to
    /// the cross-module path when the ABI lists it, since that path may bind it from the ABI (a static
    /// method, say) and reports it when it does not. When the ABI does not list it, it is reported here.
    /// </summary>
    [Theory]
    [InlineData("tag", "public static func tag() -> Swift.Int32", true, true)]
    [InlineData("pick", "public func pick<T>(_ value: T) -> Swift.Int32", false, true)]
    [InlineData("tag", "public static func tag() -> Swift.Int32", true, false)]
    [InlineData("pick", "public func pick<T>(_ value: T) -> Swift.Int32", false, false)]
    public void ProcessForeignTypeExtensions_DeclinedMemberOfACarriedClassReceiver_LeftToTheCrossModulePath(
        string name, string signature, bool isStatic, bool abiLists)
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.Types.Add(new ClassDecl
        {
            Name = "UIView",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIView"),
            MangledName = "$s5UIKit6UIViewCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        });
        var member = CreateLabeledExtMethod(name, name == "pick" ? "pick(_:)" : "tag()", signature);
        member.IsStatic = isStatic;
        if (abiLists)
        {
            var listed = AbiMethod(name, moduleDecl, moduleDecl.Types[^1], isStatic, "Swift.Int32");
            if (name == "pick")
            {
                listed.GenericParameters.Add(new GenericArgumentDecl("τ_0_0", "T", new(), new()));
                listed.CSSignature.Add(AbiArgument("_", "τ_0_0", moduleDecl));
            }
            moduleDecl.Types[^1].Methods.Add(listed);
        }

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["UIKit.UIView"] = new() { member } }, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
        if (abiLists)
        {
            Assert.Empty(report.SkippedItems);
            return;
        }
        var row = Assert.Single(report.SkippedItems);
        Assert.Equal(name, row.Name);
        Assert.Equal("UIKit.UIView", row.ContainingType);
    }

    /// <summary>
    /// An overload the ABI lists does not account for one it does not: a pre-pass decline is left to
    /// the cross-module path only for the member the ABI lists with the same labels and types.
    /// </summary>
    [Fact]
    public void ProcessForeignTypeExtensions_DeclinedOverloadTheAbiDoesNotList_Reported()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.Types.Add(new ClassDecl
        {
            Name = "UIView",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIView"),
            MangledName = "$s5UIKit6UIViewCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        });
        var listed = AbiMethod("tag", moduleDecl, moduleDecl.Types[^1], isStatic: true, "Swift.Int32");
        listed.CSSignature.Add(AbiArgument("for", "Swift.Int32", moduleDecl));
        moduleDecl.Types[^1].Methods.Add(listed);
        var sameLabels = CreateLabeledExtMethod("tag", "tag(for:)", "public static func tag(for value: Swift.Int32) -> Swift.Int32");
        sameLabels.IsStatic = true;
        var otherType = CreateLabeledExtMethod("tag", "tag(for:)", "public static func tag(for value: Swift.Double) -> Swift.Int32");
        otherType.IsStatic = true;
        var otherLabels = CreateLabeledExtMethod("tag", "tag(at:)", "public static func tag(at value: Swift.Int32) -> Swift.Int32");
        otherLabels.IsStatic = true;

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["UIKit.UIView"] = new() { sameLabels, otherType, otherLabels } }, ctx);

        Assert.Equal(2, report.SkippedItems.Count);
        Assert.All(report.SkippedItems, r => Assert.Equal("tag", r.Name));
    }

    private static MethodDecl AbiMethod(string name, ModuleDecl moduleDecl, TypeDecl parent, bool isStatic, string returnType) => new()
    {
        Name = name,
        MangledName = $"$s5UIKit6UIViewC{name.Length}{name}",
        MethodType = isStatic ? MethodType.Static : MethodType.Instance,
        IsConstructor = false,
        Throws = false,
        IsAsync = false,
        CSSignature = new List<ArgumentDecl> { AbiArgument(string.Empty, returnType, moduleDecl) },
        GenericParameters = new List<GenericArgumentDecl>(),
        ParentDecl = parent,
        ModuleDecl = moduleDecl,
        IsSynthesizedAccessor = false,
    };

    private static ArgumentDecl AbiArgument(string label, string type, ModuleDecl moduleDecl) => new()
    {
        Name = label,
        PrivateName = label,
        SwiftTypeSpec = new NamedTypeSpec(type),
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = moduleDecl,
    };

    private static PropertyDecl AbiProperty(string name, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
        IsStatic = false,
        HasStorage = false,
        Accessors = new List<AccessorDecl>(),
        ParentDecl = null,
        ModuleDecl = moduleDecl,
    };

    /// <summary>
    /// What the parser parked with a dropped receiver is published once emission settles, and a member
    /// the interface also lists on that receiver gets one row, not two.
    /// </summary>
    [Fact]
    public void RepublishWithdrawals_ParserParkedMembers_ReportedOnce()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.ForeignTypeDeclKinds["Foundation.AttributeScopes"] = "Enum";
        moduleDecl.UnboundForeignReceiverMembers.Add(new UnboundForeignReceiverMember(
            "Foundation.AttributeScopes", BindingItemKind.Type, "TranslationAttributes", "Not bound: parked"));
        moduleDecl.UnboundForeignReceiverMembers.Add(new UnboundForeignReceiverMember(
            "Foundation.AttributeScopes", BindingItemKind.Property, "translation", "Not bound: parked"));
        var property = CreateExtMethod("translation", "public var translation: Swift.Int { get }");
        property.IsProperty = true;

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["Foundation.AttributeScopes"] = new() { property } }, ctx);

        Assert.Equal(
            new[] { "TranslationAttributes|Type", "translation|Property" },
            report.SkippedItems.Select(r => $"{r.Name}|{r.Kind}").OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(report.SkippedItems, r =>
        {
            Assert.Equal("Foundation.AttributeScopes", r.ContainingType);
            Assert.Equal("Not bound: parked", r.Details);
        });
    }

    /// <summary>
    /// Overloads parked with a dropped receiver share a name but not a symbol, and each loss keeps a row.
    /// </summary>
    [Fact]
    public void RepublishWithdrawals_ParkedOverloads_EachReported()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.UnboundForeignReceiverMembers.Add(new UnboundForeignReceiverMember(
            "Foundation.AttributeScopes", BindingItemKind.Method, "describe", "Not bound: parked", "$s_describe_Int32"));
        moduleDecl.UnboundForeignReceiverMembers.Add(new UnboundForeignReceiverMember(
            "Foundation.AttributeScopes", BindingItemKind.Method, "describe", "Not bound: parked", "$s_describe_String"));

        var report = RunPrePassAcrossASessionRestart(moduleDecl, new(), ctx);

        Assert.Equal(2, report.SkippedItems.Count(r => r.Name == "describe"));
    }

    /// <summary>
    /// An interface member is left to the parser's parked row only when that row is the same member:
    /// an overload of the same name with other labels, or the other staticness, keeps a row of its
    /// own, and the parked member is reported once.
    /// </summary>
    [Theory]
    [InlineData("public func describe(with value: Swift.Int32)", false, 1)]
    [InlineData("public func describe(as value: Swift.Int32)", false, 2)]
    [InlineData("public static func describe(with value: Swift.Int32)", true, 2)]
    public void RepublishWithdrawals_ParkedOverloadOfAnInterfaceMember_ReportsEachMemberOnce(
        string interfaceSignature, bool interfaceIsStatic, int expectedRows)
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.ForeignTypeDeclKinds["Foundation.AttributeScopes"] = "Enum";
        moduleDecl.UnboundForeignReceiverMembers.Add(new UnboundForeignReceiverMember(
            "Foundation.AttributeScopes", BindingItemKind.Method, "describe", "Not bound: parked", "$s_describe_with",
            "describe(with:)", IsStatic: false));
        var member = CreateExtMethod("describe", interfaceSignature);
        member.PrintedName = interfaceSignature.Contains("(as ", StringComparison.Ordinal) ? "describe(as:)" : "describe(with:)";
        member.IsStatic = interfaceIsStatic;

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["Foundation.AttributeScopes"] = new() { member } }, ctx);

        Assert.Equal(expectedRows, report.SkippedItems.Count(r => r.Name == "describe"));
    }

    /// <summary>
    /// A parked member pairs with the interface overload whose parameter types read the same, wherever
    /// that overload is declared: a same-labelled overload of another type keeps its own row.
    /// </summary>
    [Fact]
    public void RepublishWithdrawals_ParkedOverloadWithOtherTypes_KeepsTheInterfaceMembersRow()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.ForeignTypeDeclKinds["Foundation.AttributeScopes"] = "Enum";
        moduleDecl.UnboundForeignReceiverMembers.Add(new UnboundForeignReceiverMember(
            "Foundation.AttributeScopes", BindingItemKind.Method, "describe", "Not bound: parked", "$s_describe_with",
            "describe(with:)", IsStatic: false, ParameterTypes: new[] { "Swift.Int32" }));
        var other = CreateLabeledExtMethod("describe", "describe(with:)", "public func describe(with value: Swift.String)");
        var same = CreateLabeledExtMethod("describe", "describe(with:)", "public func describe(with value: Swift.Int32)");

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["Foundation.AttributeScopes"] = new() { other, same } }, ctx);

        var rows = report.SkippedItems.Where(r => r.Name == "describe").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.Details == "Not bound: parked");
        Assert.Single(rows, r => r.Details!.Contains("Swift enum", StringComparison.Ordinal));
    }

    /// <summary>
    /// On a carried receiver, each initializer or subscript the ABI lists accounts for the one
    /// declaration of its labels and parameter types; other initializers and subscripts keep rows.
    /// </summary>
    [Fact]
    public void RecordDroppedExtensionCandidates_CarriedInitializerAndSubscriptOverloads_EachListedMemberCoversOne()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        const string receiver = "Foundation.Measurement";
        moduleDecl.ForeignTypeDeclKinds[receiver] = "Struct";
        var carried = CarriedStruct(moduleDecl, receiver);
        var init = AbiMethod("init", moduleDecl, carried, isStatic: false, "Foundation.Measurement");
        init.IsConstructor = true;
        init.CSSignature.Add(AbiArgument("seed", "Swift.Int32", moduleDecl));
        carried.Methods.Add(init);
        carried.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int32"),
            IndexParameters = new List<ArgumentDecl> { AbiArgument("i", "Swift.Int32", moduleDecl) },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            MangledName = "$s_subscript_Int32",
            ParentDecl = carried,
            ModuleDecl = moduleDecl,
        });
        carried.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            PrintedName = "subscript(a:)",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Double"),
            IndexParameters = new List<ArgumentDecl> { AbiArgument("a", "Swift.Double", moduleDecl) },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            MangledName = "$s_subscript_a_Double",
            ParentDecl = carried,
            ModuleDecl = moduleDecl,
        });
        DroppedExtensionCandidate Candidate(string kind, string name, string printedName, string signature) => new()
        {
            ExtendedTypeName = receiver, Kind = kind, Name = name, PrintedName = printedName,
            RawSignature = signature, IsStatic = false, Reason = $"not read: {signature}",
        };

        var report = RecordAndRepublish(moduleDecl, ctx, new[]
        {
            Candidate("initializer", "init", "init(seed:)", "public init(seed: Swift.String)"),
            Candidate("initializer", "init", "init(seed:)", "public init(seed: Swift.Int32)"),
            Candidate("initializer", "init", "init(other:)", "public init(other: Swift.Int32)"),
            Candidate("subscript", "subscript", "subscript(_:)", "public subscript(i: Swift.String) -> Swift.Int32"),
            Candidate("subscript", "subscript", "subscript(_:)", "public subscript(i: Swift.Int32) -> Swift.Int32"),
            Candidate("subscript", "subscript", "subscript(b:)", "public subscript(b x: Swift.Double) -> Swift.Double"),
            Candidate("subscript", "subscript", "subscript(a:)", "public subscript(a x: Swift.Double) -> Swift.Double"),
        });

        Assert.Equal(
            new[]
            {
                "not read: public init(other: Swift.Int32)",
                "not read: public init(seed: Swift.String)",
                "not read: public subscript(b x: Swift.Double) -> Swift.Double",
                "not read: public subscript(i: Swift.String) -> Swift.Int32",
            },
            report.SkippedItems.Select(r => r.Details![(r.Details!.IndexOf("not read", StringComparison.Ordinal))..^1])
                .OrderBy(d => d, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Two declarations of one shape in differently constrained extensions are two members, and each
    /// keeps a row.
    /// </summary>
    [Fact]
    public void RecordDroppedExtensionCandidates_SameDeclarationUnderDifferentConstraints_KeepsARowEach()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.ForeignTypeDeclKinds["Foundation.AttributeScopes"] = "Enum";
        DroppedExtensionCandidate Candidate(string whereClause) => new()
        {
            ExtendedTypeName = "Foundation.AttributeScopes", Kind = "subscript", Name = "subscript",
            PrintedName = "subscript(_:)", RawSignature = "public subscript(i: Swift.Int) -> Swift.Int",
            WhereClause = whereClause, IsStatic = false, Reason = "not read",
        };

        var report = RecordAndRepublish(moduleDecl, ctx, new[] { Candidate("where T == Swift.Int"), Candidate("where T == Swift.String") });

        Assert.Equal(2, report.SkippedItems.Count(r => r.Kind == BindingItemKind.Subscript));
    }

    /// <summary>
    /// A receiver whose kind the ABI does not name may be a protocol: a declaration that supplies the
    /// default of one of its requirements (a subscript keyed by its parameter types) is reached
    /// through the requirement, and the others keep rows.
    /// </summary>
    [Fact]
    public void RecordDroppedExtensionCandidates_ReceiverOfUnknownKind_RequirementDefaultsAreNotReported()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var protocol = new ProtocolDecl
        {
            Name = "Indexed",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Dep.Indexed"),
            MangledName = "$s3Dep7IndexedP",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        protocol.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl> { AbiArgument("i", "Swift.Int", moduleDecl) },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            MangledName = "$s_req_subscript",
            ParentDecl = protocol,
            ModuleDecl = moduleDecl,
        });
        moduleDecl.DependencyProtocols["Dep"] = new List<ProtocolDecl> { protocol };
        DroppedExtensionCandidate Candidate(string type, string result) => new()
        {
            ExtendedTypeName = "Dep.Indexed", Kind = "subscript", Name = "subscript",
            PrintedName = "subscript(_:)", RawSignature = $"public subscript(i: {type}) -> {result}",
            IsStatic = false, Reason = $"not read: {type} -> {result}",
        };

        var report = RecordAndRepublish(moduleDecl, ctx, new[]
        {
            Candidate("Swift.Int", "Swift.Int"), Candidate("Swift.String", "Swift.Int"), Candidate("Swift.Int", "Swift.String"),
        });

        var rows = report.SkippedItems.Where(r => r.Kind == BindingItemKind.Subscript).ToList();
        Assert.Equal(
            new[] { "Swift.Int -> Swift.String", "Swift.String -> Swift.Int" },
            rows.Select(r => r.Details![(r.Details!.IndexOf("not read: ", StringComparison.Ordinal) + 10)..^1])
                .OrderBy(d => d, StringComparer.Ordinal).ToArray());
        Assert.All(rows, r => Assert.Equal("Dep.Indexed", r.ContainingType));
    }

    /// <summary>
    /// A parked member pairs with an interface overload only when it is public and their types may be
    /// one spelling: the same types, or a name the databases do not hold where the ABI resolves a
    /// typealias. A tuple where the interface names a known type, or an internal sibling, is another
    /// member, and the interface overload keeps its row.
    /// </summary>
    [Theory]
    [InlineData("(Swift.Int, Swift.Int)", true, "Swift.Int", 2)]
    [InlineData("Swift.Int", false, "Swift.Int", 2)]
    [InlineData("Swift.Int", true, "Swift.Int", 1)]
    [InlineData("Foundation.Date", true, "Foundation.KitchenAlias", 1)]
    public void RepublishWithdrawals_ParkedMember_PairsOnlyWithAPublicMemberItMayBe(
        string parkedType, bool parkedIsPublic, string interfaceType, int expectedRows)
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.ForeignTypeDeclKinds["Foundation.AttributeScopes"] = "Enum";
        moduleDecl.UnboundForeignReceiverMembers.Add(new UnboundForeignReceiverMember(
            "Foundation.AttributeScopes", BindingItemKind.Method, "f", "Not bound: parked", "$s_f",
            "f(x:)", IsStatic: false, ParameterTypes: new[] { parkedType }, ReturnType: "()", IsPublic: parkedIsPublic));
        var member = CreateLabeledExtMethod("f", "f(x:)", $"public func f(x: {interfaceType})");

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["Foundation.AttributeScopes"] = new() { member } }, ctx);

        Assert.Equal(expectedRows, report.SkippedItems.Count(r => r.Name == "f"));
    }

    /// <summary>Overloads that differ only in their return type are two members.</summary>
    [Fact]
    public void RepublishWithdrawals_ParkedReturnOnlyOverload_KeepsTheOtherOverloadsRow()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.ForeignTypeDeclKinds["Foundation.AttributeScopes"] = "Enum";
        moduleDecl.UnboundForeignReceiverMembers.Add(new UnboundForeignReceiverMember(
            "Foundation.AttributeScopes", BindingItemKind.Method, "f", "Not bound: parked", "$s_f_String",
            "f()", IsStatic: false, ParameterTypes: Array.Empty<string>(), ReturnType: "Swift.String"));
        var integer = CreateLabeledExtMethod("f", "f()", "public func f() -> Swift.Int32");
        var text = CreateLabeledExtMethod("f", "f()", "public func f() -> Swift.String");

        var report = RunPrePassAcrossASessionRestart(
            moduleDecl, new() { ["Foundation.AttributeScopes"] = new() { integer, text } }, ctx);

        var rows = report.SkippedItems.Where(r => r.Name == "f").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.Details == "Not bound: parked");
    }

    /// <summary>
    /// A foreign protocol the parser kept for its extension members has those members reported by the
    /// protocol-extension path, so a declaration the interface reader did not take is left to it when
    /// the ABI lists it there, and keeps a row of its own when it does not.
    /// </summary>
    [Fact]
    public void RecordDroppedExtensionCandidates_RetainedForeignProtocol_LeavesListedMembersToItsReport()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var protocol = new ProtocolDecl
        {
            Name = "Cancellable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Combine.Cancellable"),
            MangledName = "$s7Combine11CancellableP",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        protocol.ExtensionMembers.Add(new SubscriptDecl
        {
            Name = "subscript",
            PrintedName = "subscript(_:)",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int32"),
            IndexParameters = new List<ArgumentDecl> { AbiArgument("i", "Swift.Int32", moduleDecl) },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            MangledName = "$s_ext_subscript",
            ParentDecl = protocol,
            ModuleDecl = moduleDecl,
        });
        moduleDecl.Protocols.Add(protocol);
        DroppedExtensionCandidate Candidate(string type) => new()
        {
            ExtendedTypeName = "Combine.Cancellable", Kind = "subscript", Name = "subscript",
            PrintedName = "subscript(_:)", RawSignature = $"public subscript(i: {type}) -> Swift.Int32",
            IsStatic = false, Reason = $"not read: {type}",
        };

        var report = RecordAndRepublish(moduleDecl, ctx, new[] { Candidate("Swift.Int32"), Candidate("Swift.String") });

        var row = Assert.Single(report.SkippedItems, r => r.Kind == BindingItemKind.Subscript);
        Assert.Contains("Swift.String", row.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// An operator requirement whose declaration shows its types is supplied only by an operator of
    /// those types; one shown by its spelling alone is supplied by any operator of that spelling.
    /// </summary>
    [Theory]
    [InlineData("Self", true)]
    [InlineData("Swift.Int32", false)]
    public void Requirements_TypedOperator_DeclaresOnlyTheSameTypes(string rightType, bool expected)
    {
        var requirements = new ForeignProtocolRequirements.Requirements();
        requirements.Add("static +");
        requirements.AddTypedOperator("static +", ForeignProtocolRequirements.OperatorKey("+",
            ForeignOverloadPairing.SignatureOf("static func + (lhs: Self, rhs: Self) -> Self", "func", "+"), isStatic: true)!);
        var spellingOnly = new ForeignProtocolRequirements.Requirements();
        spellingOnly.Add("static +");
        var member = ForeignProtocolRequirements.OperatorKey("+",
            ForeignOverloadPairing.SignatureOf($"public static func + (lhs: Self, rhs: {rightType}) -> Self", "func", "+"), isStatic: true);

        Assert.Equal(expected, requirements.DeclaresOperator("static +", member));
        Assert.True(spellingOnly.DeclaresOperator("static +", member));
    }

    /// <summary>
    /// A subscript or operator whose types do not read (a generic one) has no typed key, so it never
    /// matches a requirement and keeps its row.
    /// </summary>
    [Fact]
    public void RequirementKeys_UnreadableSignature_MatchNoRequirement()
    {
        var generic = ForeignOverloadPairing.SignatureOf("public subscript<T>(v: T) -> Swift.Int32", "subscript");
        Assert.Null(ForeignProtocolRequirements.SubscriptKey(generic, isStatic: false));

        var requirements = new ForeignProtocolRequirements.Requirements();
        requirements.Add("static +");
        requirements.AddTypedOperator("static +", ForeignProtocolRequirements.OperatorKey("+",
            ForeignOverloadPairing.SignatureOf("static func + (lhs: Self, rhs: Self) -> Self", "func", "+"), isStatic: true)!);
        var genericOperator = ForeignProtocolRequirements.OperatorKey("+",
            ForeignOverloadPairing.SignatureOf("public static func + <T>(lhs: Self, rhs: T) -> Self", "func", "+"), isStatic: true);
        Assert.False(requirements.DeclaresOperator("static +", genericOperator));
    }

    /// <summary>
    /// What a declaration's signature reads: parameter types (a default value left out), the return
    /// type after any effects, an escaped name found, and no types for a method generic of its own.
    /// </summary>
    [Theory]
    [InlineData("public func `repeat`(count: Swift.Int = 1) throws -> Swift.Int", "func", "repeat", "Swift.Int", "Swift.Int")]
    [InlineData("public func f(x: (Swift.Int32, Swift.Int32))", "func", "f", "(Swift.Int32, Swift.Int32)", "()")]
    [InlineData("public func f(x: Swift.String = \"a)b\") -> Swift.Int", "func", "f", "Swift.String", "Swift.Int")]
    [InlineData("public init?(seed: Swift.Int, other: [Swift.String])", "init", null, "Swift.Int|[Swift.String]", null)]
    [InlineData("public subscript(a x: Swift.Int) -> Swift.Int { get }", "subscript", null, "Swift.Int", "Swift.Int")]
    [InlineData("public func g<T>(x: T) -> T where T : Swift.Equatable", "func", "g", null, "T")]
    [InlineData("public func h() async -> Swift.Int where Self : Swift.Equatable", "func", "h", "", "Swift.Int")]
    [InlineData("public func e() throws(Kit.Failure<(Swift.Int32, Swift.Int32)>) -> Swift.Int32", "func", "e", "", "Swift.Int32")]
    public void SignatureOf_ReadsTypesAndReturn(string declaration, string keyword, string name, string parameters, string returnType)
    {
        var signature = ForeignOverloadPairing.SignatureOf(declaration, keyword, name);

        Assert.NotNull(signature);
        Assert.Equal(parameters, signature!.Parameters is null ? null : string.Join("|", signature.Parameters));
        Assert.Equal(returnType, signature.Return);
    }

    private static BindingReport RecordAndRepublish(
        ModuleDecl moduleDecl, ModuleEmissionContext ctx, IEnumerable<DroppedExtensionCandidate> candidates)
    {
        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.RecordDroppedExtensionCandidates(moduleDecl, candidates, CreateTypeDatabase(), Logger, ctx);
        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.RepublishWithdrawals(moduleDecl, ctx);
        var report = ReportCollector.Complete()!;
        ReportCollector.Reset();
        return report;
    }

    private static StructDecl CarriedStruct(ModuleDecl moduleDecl, string receiver)
    {
        var carried = new StructDecl
        {
            Name = receiver[(receiver.IndexOf('.') + 1)..],
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(receiver),
            MangledName = "$s_carried",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "",
        };
        moduleDecl.Types.Add(carried);
        return carried;
    }

    /// <summary>
    /// A public declaration the interface reader did not take (an initializer, an operator, a member
    /// with modifiers it does not read) keeps a row unless another path binds or reports it: the
    /// parser's parked member of a dropped receiver, or the ABI's member of a carried one. A
    /// typealias has no member to lose.
    /// </summary>
    [Theory]
    [InlineData("initializer", "init", "init(seed:)", "parked-same", 0)]
    [InlineData("initializer", "init", "init(seed:)", "parked-other-labels", 1)]
    [InlineData("initializer", "init", "init(seed:)", "none", 1)]
    [InlineData("operator", "+", "+(_:_:)", "none", 1)]
    [InlineData("typealias", "Alias", "Alias", "none", 0)]
    [InlineData("method", "sealed", "sealed()", "carried-listed", 0)]
    [InlineData("method", "sealed", "sealed()", "carried-unlisted", 1)]
    public void RecordDroppedExtensionCandidates_ReportsOnlyWhatNoOtherPathCovers(
        string kind, string name, string printedName, string coverage, int expectedRows)
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var receiver = "Foundation.AttributeScopes";
        if (coverage.StartsWith("carried", StringComparison.Ordinal))
        {
            receiver = "Foundation.Measurement";
            moduleDecl.ForeignTypeDeclKinds[receiver] = "Struct";
            moduleDecl.Types.Add(new StructDecl
            {
                Name = "Measurement",
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(receiver),
                MangledName = "$s10Foundation11MeasurementV",
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                Subscripts = new List<SubscriptDecl>(),
                GenericParameters = new List<GenericArgumentDecl>(),
                Conformances = new List<TypeConformance>(),
                ParentDecl = moduleDecl,
                ModuleDecl = moduleDecl,
                IsFrozen = true,
                MetadataAccessor = "",
            });
            if (coverage == "carried-listed")
                moduleDecl.Types[^1].Methods.Add(AbiMethod(name, moduleDecl, moduleDecl.Types[^1], isStatic: false, "Swift.Void"));
        }
        else
        {
            moduleDecl.ForeignTypeDeclKinds[receiver] = "Enum";
        }
        if (coverage.StartsWith("parked", StringComparison.Ordinal))
            moduleDecl.UnboundForeignReceiverMembers.Add(new UnboundForeignReceiverMember(
                receiver, BindingItemKind.Method, "init", "Not bound: parked", "$s_init",
                coverage == "parked-same" ? "init(seed:)" : "init(other:)", IsStatic: false));
        var candidate = new DroppedExtensionCandidate
        {
            ExtendedTypeName = receiver,
            Kind = kind,
            Name = name,
            PrintedName = printedName,
            RawSignature = $"public {kind} {printedName}",
            IsStatic = kind == "operator",
            Reason = "not read",
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.RecordDroppedExtensionCandidates(moduleDecl, new[] { candidate }, CreateTypeDatabase(), Logger, ctx);
        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.RepublishWithdrawals(moduleDecl, ctx);
        var report = ReportCollector.Complete()!;
        ReportCollector.Reset();

        var rows = report.SkippedItems.Where(r => r.Name == name && r.Details.Contains("not read", StringComparison.Ordinal)).ToList();
        Assert.Equal(expectedRows, rows.Count);
        Assert.All(rows, r => Assert.Equal(receiver, r.ContainingType));
    }

    #endregion

    #region ProcessForeignTypeExtensions: property getter

    [Fact]
    public void ProcessForeignTypeExtensions_PropertyGetter_Emitted()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var property = CreateExtMethod("isEnabled", "public var isEnabled: Swift.Bool { get }");
        property.IsProperty = true;

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { property }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
    }

    /// <summary>
    /// Foundation holds Swift enums as well as ObjC classes. An extension on one
    /// (<c>Foundation.AttributeScopes</c>, a caseless Swift enum) must not take the class-receiver
    /// path, whose wrapper reads <c>self</c> through <c>Unmanaged&lt;T&gt;</c> and does not compile
    /// for a value type. The ABI dump's declared kind decides, not the module.
    /// </summary>
    [Theory]
    [InlineData("Enum", 0)]
    [InlineData("Struct", 0)]
    [InlineData("Protocol", 0)]
    [InlineData("Class", 1)]
    public void ProcessForeignTypeExtensions_DeclaredKindDecides_NotTheModule(string declaredKind, int expectedEmitted)
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.ForeignTypeDeclKinds["Foundation.AttributeScopes"] = declaredKind;
        var typeDatabase = CreateTypeDatabase();

        var property = CreateExtMethod("depth", "public var depth: Swift.Int { get }");
        property.IsProperty = true;

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["Foundation.AttributeScopes"] = new() { property }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(expectedEmitted, ctx.ForeignExtEmittedCount);
        if (expectedEmitted == 0)
            Assert.DoesNotContain(ctx.ForeignExtSwiftWrapperLines, l => l.Contains("Unmanaged<Foundation.AttributeScopes>", StringComparison.Ordinal));
    }

    [Fact]
    public void IsForeignObjCClassType_WithoutADeclaredKind_KeepsTheModuleAnswer()
    {
        var kinds = new Dictionary<string, string> { ["Foundation.Other"] = "Enum" };
        Assert.True(ForeignTypeExtensionEmitter.IsForeignObjCClassType("UIKit.UIView", kinds));
        Assert.True(ForeignTypeExtensionEmitter.IsForeignObjCClassType("UIKit.UIView"));
        Assert.False(ForeignTypeExtensionEmitter.IsForeignObjCClassType("Swift.Int"));
    }

    [Fact]
    public void ProcessForeignTypeExtensions_FrozenStructPropertyGetter_Skipped()
    {
        // UIKit.UIEdgeInsets is a frozen struct from a UIKit extension on UILabel;
        // the wrapper switch has no FrozenStruct arm so accepting it produces an empty
        // C# body and a void-return P/Invoke. TryProcessProperty must reject FrozenStruct
        // for parity with TryProcessMethod.
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var uikitModule = new ModuleTypeDatabase("UIKit", "/System/Library/Frameworks/UIKit.framework/UIKit");
        uikitModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("UIKit.UIEdgeInsets"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIEdgeInsets"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIEdgeInsets"),
                MetadataAccessor = "$sSo12UIEdgeInsetsVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(uikitModule);

        var property = CreateExtMethod("skeletonPaddingInsets",
            "public var skeletonPaddingInsets: UIKit.UIEdgeInsets { get set }");
        property.IsProperty = true;
        property.HasSetter = true;

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UILabel"] = new() { property }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: method with void return

    [Fact]
    public void ProcessForeignTypeExtensions_VoidMethod_Emitted()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("configure", "public func configure()");

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: generic method skipped

    [Fact]
    public void ProcessForeignTypeExtensions_GenericMethod_Skipped()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("transform", "public func transform<T>(_ value: T) -> T");

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region EmitSwiftWrappers: emits wrappers

    [Fact]
    public void EmitSwiftWrappers_WithProcessedMembers_EmitsOutput()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("configure", "public func configure()");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        ForeignTypeExtensionEmitter.EmitSwiftWrappers(swiftWriter, ctx);

        var result = swiftOutput.ToString();
        Assert.Contains("@_silgen_name", result);
        // SBSW_ (Swift CC wrapper convention) — foreign-type extension wrappers stay on
        // @_silgen_name so SwiftIndirectResult maps correctly, and PInvokeEmitHelper
        // enforces SBW_ ↔ Cdecl exclusively. See ForeignTypeExtensionEmitter.BuildSymbolName.
        Assert.Contains("SBSW_", result);
        Assert.Contains("UIView", result);
        Assert.Contains("Unmanaged", result);
    }

    /// <summary>
    /// Each member the module adds to a foreign type is its own recovery unit: its wrapper and its C#
    /// surface are written as that member's fragments, so a swiftc error in one is charged to it, not to
    /// the module. Withdrawing that unit leaves the member out of both planes, records the withdrawal
    /// for the settled report, and keeps its sibling on the same foreign type.
    /// </summary>
    [Fact]
    public void ForeignExtensionMember_IsItsOwnRecoveryUnit_AndWithdrawsAlone()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var configure = CreateExtMethod("configure", "public func configure()");
        var reset = CreateExtMethod("reset", "public func reset()");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { configure, reset }
        };
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(moduleDecl, extensions, typeDatabase, Logger, ctx);
        var configureId = DeclIdFactory.ForForeignExtensionMember("UIKit.UIView", configure);
        var root = FragmentOwners.ForModule(moduleDecl);

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        ForeignTypeExtensionEmitter.EmitSwiftWrappers(swiftWriter, ctx);
        var swift = swiftOutput.ToString();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);
        var csharp = csOutput.ToString();

        RecoveryUnitId OwnerAt(BufferedSourceWriter writer, string text, string marker)
        {
            var offset = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(offset >= 0, $"'{marker}' was not emitted");
            var tile = writer.Fragments.BuildTiling(text.Length, root).Single(t => t.Start <= offset && offset < t.End);
            return RecoveryUnitClassifier.ClassifyArtifact(tile.Owner.Artifact).Unit;
        }
        var swiftUnit = OwnerAt(swiftWriter, swift, "_configure");
        var csharpUnit = OwnerAt(csWriter, csharp, "Configure(");
        Assert.Equal(RecoveryScope.LeafApi, swiftUnit.Scope);
        Assert.Equal(configureId, swiftUnit.Decl);
        Assert.Equal(swiftUnit, csharpUnit);
        Assert.NotEqual(swiftUnit, OwnerAt(swiftWriter, swift, "_reset"));

        var withdrawnSwift = new StringWriter();
        var withdrawnCSharp = new StringWriter();
        BindingReport report = null;
        ReportCollector.Start(moduleDecl);
        try
        {
            using (EmissionAttempt.Begin(WrapperDenylistSeed.Build(
                new HashSet<RecoveryUnitId> { swiftUnit }, static _ => EmitterFaultOrigin.RecoveryWithdrawal)))
            {
                ForeignTypeExtensionEmitter.EmitSwiftWrappers(new SwiftWriter(withdrawnSwift), ctx);
                ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(new CSharpWriter(withdrawnCSharp), typeDatabase, "TestModule", ctx, moduleDecl);
            }
        }
        finally
        {
            report = ReportCollector.Complete();
            ReportCollector.Reset();
        }

        Assert.DoesNotContain("_configure", withdrawnSwift.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Configure(", withdrawnCSharp.ToString(), StringComparison.Ordinal);
        Assert.Contains("_reset", withdrawnSwift.ToString(), StringComparison.Ordinal);
        Assert.Contains("Reset(", withdrawnCSharp.ToString(), StringComparison.Ordinal);
        var withdrawal = Assert.Single(report.SkippedItems, item => item.Reason == SkipReason.EmitterFault);
        Assert.Equal("configure", withdrawal.Name);
        // Keyed by the denied unit's own identity, so an overload or a same-named member of another
        // receiver keeps its own row.
        Assert.Equal(configureId.Canonical, withdrawal.DeclId);
        Assert.Equal("UIKit.UIView", withdrawal.ContainingType);
        // The row belongs to this attempt alone: nothing outlives it to be republished after a later
        // render that ships the member.
        Assert.Empty(ctx.ForeignExtWithdrawals);
    }

    [Fact]
    public void ProcessedMember_DescribesTheWrapperItsSwiftConventionPInvokeBinds()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { CreateExtMethod("configure", "public func configure()") }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(moduleDecl, extensions, typeDatabase, Logger, ctx);
        var swiftOutput = new StringWriter();
        ForeignTypeExtensionEmitter.EmitSwiftWrappers(new SwiftWriter(swiftOutput), ctx);
        var csOutput = new StringWriter();
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(new CSharpWriter(csOutput), typeDatabase, "TestModule", ctx);

        var swift = swiftOutput.ToString();
        var wrapper = Assert.Single(SyntheticRegistration.AssertRegistered(ctx, swift, SwiftWrapperSynthesizer.ForeignTypeExtension));
        SyntheticRegistration.AssertEveryWrittenIsRegistered(ctx, swift, "SBSW_");
        // A free function that takes the foreign instance as its first parameter.
        Assert.Equal(SwiftCallSelfKind.None, wrapper.Self.Kind);
        Assert.NotEmpty(wrapper.Parameters);
        SyntheticRegistration.AssertBoundWithFacts(ctx, csOutput.ToString(), SwiftWrapperSynthesizer.ForeignTypeExtension);
    }

    [Fact]
    public void EmitSwiftWrappers_UserParamNamedSelf_EscapedAgainstInjectedReceiver()
    {
        // Foreign-type-extension path: a user parameter literally named `self_` collides with the
        // receiver pointer the wrapper injects (`_ self_: UnsafeMutableRawPointer`). Without the
        // reserved-escape in ComputeForeignExtParamNames, the wrapper declares `self_` twice; swiftc
        // rejects it and the entry point is silently dropped from the dylib (runtime
        // EntryPointNotFoundException). The fix escapes the user binding to `__self_`.
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("shift", "public func shift(self_: Swift.Int) -> Swift.Int");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        ForeignTypeExtensionEmitter.EmitSwiftWrappers(swiftWriter, ctx);
        var result = swiftOutput.ToString();

        // A wrapper was emitted at all.
        Assert.Contains("SBSW_", result);
        // The injected receiver binding (`_ self_:`) must appear EXACTLY once. Parameter decls carry
        // the `_ ` positional prefix; the forwarded call label (`self_:`) does not, so this literal
        // isolates the binding decl. Pre-fix the user param re-declared `_ self_:` → count 2.
        Assert.Single(Regex.Matches(result, "_ self_:"));
        // The colliding user binding was escaped rather than re-declared.
        Assert.Contains("__self_", result);
        // ...and the rename is SOURCE-LOCAL only: the forwarded Swift call must still pass the value
        // under the original external argument label `self_:` (computed from the Swift label, not the
        // escaped binding). `self_: __self_` proves label and binding moved independently — escaping
        // the binding without also rewriting the call label would forward under the wrong label and
        // silently change which Swift parameter receives the value.
        Assert.Contains("self_: __self_", result);
    }

    // An Objective-C class built inside the extension member is held only by the wrapper's own
    // `result` local, which Swift releases when the wrapper returns. Handing it back through
    // passUnretained returned a pointer to a freed object; it has to cross at +1, and the C# side
    // has to adopt that reference rather than retain it again.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObjCClassReturn_CrossesAtPlusOneAndIsAdoptedByTheReader(bool asProperty)
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var member = asProperty
            ? CreateExtMethod("snapshotView", "public var snapshotView: UIKit.UIView { get }")
            : CreateExtMethod("makeSnapshot", "public func makeSnapshot() -> UIKit.UIView");
        member.IsProperty = asProperty;
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { member }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);
        Assert.Equal(1, ctx.ForeignExtEmittedCount);

        var swiftOutput = new StringWriter();
        ForeignTypeExtensionEmitter.EmitSwiftWrappers(new SwiftWriter(swiftOutput), ctx);
        var swift = swiftOutput.ToString();
        Assert.Contains("Swift.Unmanaged.passRetained(result).toOpaque()", swift);
        Assert.DoesNotContain("passUnretained(result)", swift);

        var csOutput = new StringWriter();
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(new CSharpWriter(csOutput), typeDatabase, "TestModule", ctx);
        var cs = csOutput.ToString();
        Assert.Matches(@"GetINativeObject<UIKit\.UIView>\(\w+, true\)", cs);
        Assert.DoesNotContain("GetNSObject<UIKit.UIView>", cs);
    }

    [Fact]
    public void EmitSwiftWrappers_NoProcessedMembers_NoOutput()
    {
        var ctx = new ModuleEmissionContext();
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        ForeignTypeExtensionEmitter.EmitSwiftWrappers(swiftWriter, ctx);

        Assert.Empty(swiftOutput.ToString());
    }

    #endregion

    #region EmitCSharpExtensionClasses: emits C# extension class

    [Fact]
    public void EmitCSharpExtensionClasses_WithProcessedMembers_EmitsClass()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("configure", "public func configure()");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        var result = csOutput.ToString();
        Assert.Contains("public static partial class", result);
        Assert.Contains("Extensions", result);
        Assert.Contains("Configure", result);
        Assert.Contains("NativeMethods", result);
        Assert.Contains("LibraryImport", result);
    }

    [Fact]
    public void EmitCSharpExtensionClasses_ParameterSpelledLikeTheReceiver_KeepsTheParameterAndMovesTheReceiver()
    {
        // `self` is a legal Swift argument label, so a foreign-receiver member can project a
        // parameter spelled exactly like the receiver the emitted extension method declares. The
        // parameter is the one a caller can name at the call site, so the receiver is what moves.
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("scoredWith", "public func scoredWith(self: Swift.Int) -> Swift.Int");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        var declaration = SingleForeignDeclarationOf("ScoredWith", csOutput.ToString());
        Assert.Equal(1, CountForeignParametersNamed(declaration, "self"));
        Assert.DoesNotContain("this UIKit.UIView self,", declaration);
    }

    [Fact]
    public void EmitCSharpExtensionClasses_NoNameCollision_DeclaresTheReceiverAsSelf()
    {
        // The mint is idempotent when nothing collides, so the everyday member keeps declaring
        // exactly the receiver it always did.
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("scoredWith", "public func scoredWith(amount: Swift.Int) -> Swift.Int");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        var declaration = SingleForeignDeclarationOf("ScoredWith", csOutput.ToString());
        Assert.Contains("this UIKit.UIView self", declaration);
        Assert.Contains("amount", declaration);
    }

    [Fact]
    public void EmitCSharpExtensionClasses_ForeignMemberReturningResilientStruct_BindsAndReadsTheValueBackOutOfTheBufferItSupplied()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase(RegisterStructReturnTypes);

        var method = CreateExtMethod("tagged", "public func tagged() -> TestModule.ResilientTag");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        var result = csOutput.ToString();
        Assert.Contains("Tagged", result);
        Assert.Contains("MarshalFromSwift<TestModule.ResilientTag>", result);
    }

    [Fact]
    public void EmitCSharpExtensionClasses_ForeignMemberReturningFrozenStructWithReferenceFields_DeclinesRatherThanReadAnUnwrittenBuffer()
    {
        // The emitted wrapper hands this shape back in registers, so a caller-supplied buffer would
        // still hold the allocation's contents when the binding read the value out of it.
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase(RegisterStructReturnTypes);

        var method = CreateExtMethod("labeled", "public func labeled() -> TestModule.BoxedLabel");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        Assert.DoesNotContain("Labeled", csOutput.ToString());
    }

    // A genuinely resilient struct alongside a frozen one carrying reference fields. Both classify
    // as ReturnKind.NonFrozenStruct; only the first comes back through a caller-supplied buffer.
    private static void RegisterStructReturnTypes(ModuleTypeDatabase testModule)
    {
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ResilientTag"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ResilientTag"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ResilientTag"),
                MetadataAccessor = "$s10TestModule12ResilientTagVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.BoxedLabel"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BoxedLabel"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BoxedLabel"),
                MetadataAccessor = "$s10TestModule10BoxedLabelVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
    }

    // Returns the single emitted declaration line whose name matches, so a per-parameter assertion
    // reads one signature rather than the whole emitted file.
    private static string SingleForeignDeclarationOf(string emittedName, string output)
    {
        var matches = output
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("public static") && line.Contains($" {emittedName}("))
            .ToList();
        Assert.Single(matches);
        return matches[0];
    }

    // Counts parameters declared under exactly this identifier in one signature. Two of them is the
    // duplicate-parameter shape the C# compiler rejects outright (CS0100).
    private static int CountForeignParametersNamed(string declaration, string parameterName)
    {
        var open = declaration.IndexOf('(');
        var close = declaration.LastIndexOf(')');
        var parameterList = declaration.Substring(open + 1, close - open - 1);
        return parameterList
            .Split(',')
            .Count(part => part.Trim().EndsWith($" {parameterName}"));
    }

    [Fact]
    public void EmitCSharpExtensionClasses_NoProcessedMembers_NoOutput()
    {
        var ctx = new ModuleEmissionContext();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var typeDatabase = CreateTypeDatabase();

        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        Assert.Empty(csOutput.ToString());
    }

    [Fact]
    public void EmitCSharpExtensionClasses_LabelOnlyOverloads_Disambiguated()
    {
        // Swift permits overloads that differ ONLY by argument label. Both PascalCase to the
        // same C# method name with identical projected parameter types, so a naive emit produces
        // two `public static double Scaled(this UIView, double)` — CS0111. Each colliding member
        // must be renamed with a label-derived suffix.
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new()
            {
                CreateExtMethod("scaled", "public func scaled(by factor: Swift.Double) -> Swift.Double"),
                CreateExtMethod("scaled", "public func scaled(to factor: Swift.Double) -> Swift.Double"),
            }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        var result = csOutput.ToString();
        // Both overloads survive, under distinct label-derived names (not first-wins).
        Assert.Contains("ScaledBy", result);
        Assert.Contains("ScaledTo", result);
    }

    [Fact]
    public void EmitCSharpExtensionClasses_TypeDistinctOverloads_NotRenamed()
    {
        // Overloads that differ by projected parameter TYPE are legal C# overloads — they must
        // keep their natural name. Only a genuine same-name + same-signature collision is renamed;
        // "don't rename the world".
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new()
            {
                CreateExtMethod("scaled", "public func scaled(by factor: Swift.Double) -> Swift.Double"),
                CreateExtMethod("scaled", "public func scaled(from count: Swift.Int) -> Swift.Double"),
            }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        var result = csOutput.ToString();
        Assert.Contains("Scaled(", result);
        Assert.DoesNotContain("ScaledBy", result);
        Assert.DoesNotContain("ScaledFrom", result);
    }

    [Fact]
    public void EmitCSharpExtensionClasses_RenamedGroupCollidesWithNaturalSibling_BumpedNotDuplicated()
    {
        // A label suffix can collide with an UNRELATED natural sibling in the same class: the
        // singleton `scaledBy(x:)` emits its natural `ScaledBy(double)`, while the label-only
        // pair `scaled(by:)`/`scaled(to:)` renames `scaled(by:)` to the SAME `ScaledBy(double)`.
        // Reserving only within the collision group would let both emit `ScaledBy(this,double)`
        // → CS0111 again. The natural sibling's signature is reserved first, so the renamed
        // member is bumped (ScaledBy1) instead of duplicating it.
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new()
            {
                CreateExtMethod("scaledBy", "public func scaledBy(x: Swift.Double) -> Swift.Double"),
                CreateExtMethod("scaled", "public func scaled(by factor: Swift.Double) -> Swift.Double"),
                CreateExtMethod("scaled", "public func scaled(to factor: Swift.Double) -> Swift.Double"),
            }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        var result = csOutput.ToString();
        // All three members survive under DISTINCT names. Each name declares exactly one method,
        // so no two share a signature (the CS0111 guard). The bumped name proves the natural
        // sibling's key was reserved before the rename ran.
        Assert.Single(Regex.Matches(result, @"\bScaledBy\("));
        Assert.Single(Regex.Matches(result, @"\bScaledBy1\("));
        Assert.Single(Regex.Matches(result, @"\bScaledTo\("));
    }

    #endregion

    #region ProcessForeignTypeExtensions: property with setter

    [Fact]
    public void ProcessForeignTypeExtensions_PrimitivePropertyWithSetter_EmitsBothGetAndSet()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var property = CreateExtMethod("alpha", "public var alpha: Swift.Double { get set }");
        property.IsProperty = true;
        property.HasSetter = true;

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { property }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        // Should emit both getter and setter (2 members)
        Assert.Equal(2, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: method with primitive param

    [Fact]
    public void ProcessForeignTypeExtensions_MethodWithPrimitiveParam_Emitted()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("setAlpha", "public func setAlpha(value: Swift.Double)");

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: tracks foreign module imports

    [Fact]
    public void ProcessForeignTypeExtensions_TracksNeededImports()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("configure", "public func configure()");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Contains("UIKit", ctx.ForeignExtNeededImports);
    }

    #endregion

    #region ProcessForeignTypeExtensions: absent-Apple surface ingress

    // A present, non-null surface index that declares one unrelated UIKit type models "the reference
    // assembly is installed and binds UIKit, but genuinely does not contain the referenced type" — so
    // the synthesis withdraw-on-no-hit path marks the type AbsentAppleProjection deterministically.
    // Distinct from the null/surface-unavailable fallback (degrades to name synthesis, withdraws
    // nothing) and from a namespace the index never reflected (no standing to call anything absent).
    private static AppleTypeSurfaceIndex SurfaceCoveringUIKit()
    {
        var entry = new AppleTypeSurfaceEntry("UnrelatedSurfaceType", "UIKit", AppleTypeSurfaceKind.Class, null, false);
        return new(
            new Dictionary<string, AppleTypeSurfaceEntry>(System.StringComparer.Ordinal)
            {
                ["UIKit.UnrelatedSurfaceType"] = entry,
            },
            new Dictionary<string, AppleTypeSurfaceEntry>(System.StringComparer.Ordinal)
            {
                [entry.Name] = entry,
            });
    }

    [Fact]
    public void ProcessForeignTypeExtensions_MethodWithRequiredAbsentAppleParam_WithdrawnAndReported()
    {
        // UIKit.UIWindowLevel is an auto-bridge module type absent from the .NET binding surface.
        // The coarse IsCdeclCompatibleType classifier treats ANY auto-bridge module type as a
        // compatible ObjC-class pointer, so pre-fix this method emitted a phantom
        // `UIKit.UIWindowLevel` parameter reference (CS0234). The surface-authoritative gate must
        // withdraw the member — the ForeignTypeExtensionEmitter twin of the class-path withdrawal
        // MemberValidationPipeline already performs — and record a report row rather than dropping
        // it silently.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("configure", "public func configure(level: UIKit.UIWindowLevel)");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        // The phantom reference is not produced ...
        Assert.Equal(0, ctx.ForeignExtEmittedCount);
        // ... and the withdrawal is reported, not silent.
        Assert.Contains(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void ProcessForeignTypeExtensions_MethodWithAbsentAppleReturn_WithdrawnAndReported()
    {
        // A return type absent from the .NET surface would emit a dangling `UIKit.UIWindowLevel`
        // return reference (CS0234). Withdraw + report rather than emit the phantom type.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("makeLevel", "public func makeLevel() -> UIKit.UIWindowLevel");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
        Assert.Contains(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    /// <summary>
    /// On a receiver the cross-module path carries, that path may bind the member (or an overload of
    /// it) from the ABI and reports it when it does not, so the pre-pass's withdrawal of a member the
    /// ABI lists leaves no row. A member the ABI does not list keeps the withdrawal's row.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProcessForeignTypeExtensions_AbsentAppleReturnOnACarriedReceiver_LeftToTheCrossModulePath(bool abiLists)
    {
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        moduleDecl.Types.Add(new ClassDecl
        {
            Name = "UIView",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIView"),
            MangledName = "$s5UIKit6UIViewCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        });
        if (abiLists)
            moduleDecl.Types[^1].Methods.Add(AbiMethod("makeLevel", moduleDecl, moduleDecl.Types[^1], isStatic: false, "UIKit.UIWindowLevel"));
        var method = CreateExtMethod("makeLevel", "public func makeLevel() -> UIKit.UIWindowLevel");

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, new() { ["UIKit.UIView"] = new() { method } }, CreateTypeDatabase(), Logger, ctx);
        ForeignTypeExtensionEmitter.RepublishWithdrawals(moduleDecl, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
        if (abiLists)
        {
            Assert.Empty(report!.SkippedItems);
            return;
        }
        var row = Assert.Single(report!.SkippedItems);
        Assert.Equal(SkipReason.AbsentFrameworkType, row.Reason);
        Assert.Equal("makeLevel", row.Name);
    }

    [Fact]
    public void ProcessForeignTypeExtensions_PropertyWithAbsentAppleType_WithdrawnAndReported()
    {
        // A property whose type is absent from the .NET surface would emit a dangling getter return
        // reference. Withdraw + report at the property ingress too.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var property = CreateExtMethod("level", "public var level: UIKit.UIWindowLevel { get }");
        property.IsProperty = true;
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { property }
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
        Assert.Contains(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void ProcessForeignTypeExtensions_PrimitiveParam_StillEmitsUnderPresentSurface()
    {
        // Surgical-fix guard: the surface-authoritative gate must not withdraw a member whose types
        // are all known-good. A primitive parameter is unaffected by the absent-Apple withdrawal even
        // when a present-but-empty surface is installed.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("setAlpha", "public func setAlpha(value: Swift.Double)");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
        Assert.DoesNotContain(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void ProcessForeignTypeExtensions_SiblingPackageParam_UncoveredNamespace_StillEmits()
    {
        // The sibling-binding-package shape: an auto-bridge framework whose .NET types ship in a
        // separate binding package (Matter → SwiftBindings.Apple.Matter), not in the platform
        // reference assembly. The surface index reflects ONE platform assembly, so it holds no entry
        // in that namespace at all — and silence there is ignorance, not absence. Treating it as
        // absence withdraws every member that names a sibling-packaged type, deleting API from a
        // binding whose consumer references the sibling package and would have compiled fine.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("pair", "public func pair(device: Matter.MTRDevice)");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
        Assert.DoesNotContain(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void ProcessForeignTypeExtensions_SiblingPackageReturn_UncoveredNamespace_StillEmits()
    {
        // Same authority boundary on the return-type ingress: a sibling-packaged type is kept, so the
        // member survives instead of being withdrawn on an absence the index cannot establish.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("makeDevice", "public func makeDevice() -> Matter.MTRDevice");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
        Assert.DoesNotContain(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void Withdrawal_IsLostWhenTheReportSessionIsRestartedAfterThePrePass()
    {
        // Control for the republish below, and the reason it exists. This pre-pass runs before the
        // containment loop, which starts a fresh report session for EVERY attempt — so the row it
        // writes is discarded before the first attempt even renders, and the pre-pass never runs
        // again to rewrite it. Without a republish the withdrawal is simply absent from the report
        // the binding ships with, which is the one thing the partial-success contract promises it
        // will not be.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, WithdrawnMethodExtension(), typeDatabase, Logger, ctx);

        // What the containment loop does at the top of every attempt.
        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);

        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.DoesNotContain(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void RepublishWithdrawals_PutsThePrePassWithdrawalBackIntoTheSettledSession()
    {
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, WithdrawnMethodExtension(), typeDatabase, Logger, ctx);

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);

        ForeignTypeExtensionEmitter.RepublishWithdrawals(moduleDecl, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        var row = Assert.Single(report.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
        Assert.Equal("configure", row.Name);
        Assert.Equal(BindingItemKind.Method, row.Kind);
        Assert.Contains("UIWindowLevel", row.Details);
    }

    [Fact]
    public void RepublishWithdrawals_AddsNothingWhenTheOriginalRowSurvived()
    {
        // The direct call paths never enter containment, so the pre-pass's own row is still in the
        // live session. Replaying must leave the report as it was rather than double-counting the
        // same withdrawal.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, WithdrawnMethodExtension(), typeDatabase, Logger, ctx);

        ForeignTypeExtensionEmitter.RepublishWithdrawals(moduleDecl, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Single(report.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
        Assert.Equal(1, report.SkippedMembers);
    }

    /// <summary>
    /// One foreign extension whose only member names an Apple type absent from the .NET surface, so
    /// the pre-pass withdraws it and records the skip row the republish tests are about.
    /// </summary>
    private static Dictionary<string, List<ProtocolExtensionMethodDecl>> WithdrawnMethodExtension() =>
        new()
        {
            ["UIKit.UIView"] = new()
            {
                CreateExtMethod("configure", "public func configure(level: UIKit.UIWindowLevel)"),
            },
        };

    #endregion

    #region Helpers

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ProtocolExtensionMethodDecl CreateExtMethod(string methodName, string rawSignature)
    {
        return new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = "",
            MethodName = methodName,
            RawSignature = rawSignature,
            ReturnsSelf = false,
            IsMainActorIsolated = false,
            IsStatic = false,
            IsProperty = false,
            PrintedName = $"{methodName}()",
            WhereConstraints = new List<string>()
        };
    }

    private static ProtocolExtensionMethodDecl CreateLabeledExtMethod(string methodName, string printedName, string rawSignature)
    {
        var method = CreateExtMethod(methodName, rawSignature);
        method.PrintedName = printedName;
        return method;
    }

    private static TypeDatabase CreateTypeDatabase(Action<ModuleTypeDatabase> seedTestModule = null)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
                MetadataAccessor = "$sSdMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        seedTestModule?.Invoke(testModule);
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    #endregion
}
