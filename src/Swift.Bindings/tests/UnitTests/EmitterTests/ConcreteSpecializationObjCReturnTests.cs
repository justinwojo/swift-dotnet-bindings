// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A concrete specialization's <c>@_cdecl</c> wrapper hands back a returned class as a retained
/// pointer. When the class is an Objective-C class the C# side is an NSObject wrapper, which has no
/// Swift payload factory, so the member must adopt the pointer through the Objective-C runtime.
/// Treating it as a Swift object produced C# that did not compile (CS0311) and failed the binding.
/// </summary>
public class ConcreteSpecializationObjCReturnTests
{
    private static TypeDatabase Db()
    {
        var db = new TypeDatabase();
        var uikit = new ModuleTypeDatabase("UIKit", "/System/Library/Frameworks/UIKit.framework/UIKit");
        uikit.RegisterType(SwiftTypeName.FromModuleQualifiedName("UIKit.UIColor"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIColor"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIColor"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        db.AddModuleDatabase(uikit);
        var module = new ModuleTypeDatabase("M", "/tmp/M.dylib");
        module.RegisterType(SwiftTypeName.FromModuleQualifiedName("M.Box"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("M", "Box"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("M.Box"),
            MetadataAccessor = "$s1M3BoxCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        db.AddModuleDatabase(module);
        return db;
    }

    [Theory]
    [InlineData("UIKit.UIColor", "global::UIKit.UIColor")]
    [InlineData("Swift.Optional<UIKit.UIColor>", "global::UIKit.UIColor?")]
    [InlineData("M.Box", null)]
    [InlineData("Swift.Optional<M.Box>", null)]
    [InlineData("Swift.Int", null)]
    public void ObjCObjectReturn_SeparatesObjectiveCClassesFromSwiftClasses(string spelling, string? expected)
    {
        var found = ConcreteProtocolSpecializationEmitter.TryGetObjCObjectReturnType(TypeSpecParser.Parse(spelling)!, Db(), out var csharpType);
        Assert.Equal(expected is not null, found);
        Assert.Equal(expected, csharpType);
    }

    [Fact]
    public void APlatformClassNoDatabaseDeclares_IsNamedByItsNamespace()
    {
        // A platform class the module databases do not list resolves to a synthesized record. The
        // public signature must spell it with its namespace, since a generated file imports none.
        var found = ConcreteProtocolSpecializationEmitter.TryGetObjCObjectReturnType(TypeSpecParser.Parse("UIKit.UIBezierPath")!, Db(), out var csharpType);
        Assert.True(found);
        Assert.Equal("global::UIKit.UIBezierPath", csharpType);
    }

    [Fact]
    public void AnObjectiveCClassReturn_AdoptsTheRetainedPointer()
    {
        // The wrapper returns Unmanaged.passRetained(...).toOpaque(): the managed peer takes the +1.
        var call = MarshallingHelpers.FormatObjCBridgeCall("UIKit.UIColor", "_result", nonNull: true, ownsReference: true);
        Assert.Contains("GetINativeObject<UIKit.UIColor>(_result, true)", call);
    }
}
