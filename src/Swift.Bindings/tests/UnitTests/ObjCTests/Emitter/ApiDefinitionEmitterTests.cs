// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

public class ApiDefinitionEmitterTests
{
    static readonly Microsoft.Extensions.Logging.ILogger Logger = NullLogger.Instance;

    // Helper to emit and read back the file content
    static string EmitAndRead(ObjCModule module, string ns = "TestNamespace")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"apidefinition_test_{Guid.NewGuid():N}");
        try
        {
            var path = ApiDefinitionEmitter.Emit(module, dir, ns, Logger);
            Assert.Equal(Path.Combine(dir, "ApiDefinition.cs"), path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    // --- Simple class emission ---

    [Fact]
    public void Emit_SimpleClass_HasBaseType()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl { Name = "MyClass" }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[BaseType(typeof(NSObject))]", result);
        Assert.Contains("partial interface MyClass", result);
    }

    [Fact]
    public void Emit_ClassWithSuperclass_UsesSuperclassInBaseType()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl { Name = "MyView", SuperclassName = "UIView" }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[BaseType(typeof(UIView))]", result);
        Assert.Contains("partial interface MyView", result);
    }

    [Fact]
    public void Emit_ClassWithProtocolAdoption_EmitsInterfaceList()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                ProtocolNames = ["UITableViewDelegate", "UIScrollViewDelegate"]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("partial interface MyClass : IUITableViewDelegate, IUIScrollViewDelegate", result);
    }

    // --- Constructor ---

    [Fact]
    public void Emit_InitSelector_EmitsConstructor()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "init",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"init\")]", result);
        Assert.Contains("NativeHandle Constructor();", result);
    }

    [Fact]
    public void Emit_InitWithSelector_EmitsConstructorWithParams()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "initWithFoo:bar:",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true,
                    Parameters =
                    [
                        new ObjCParameterDecl { Name = "foo", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } },
                        new ObjCParameterDecl { Name = "bar", Type = new ObjCTypeRef { Name = "NSInteger" } }
                    ]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"initWithFoo:bar:\")]", result);
        Assert.Contains("NativeHandle Constructor(string foo, nint bar);", result);
    }

    // --- Instance method ---

    [Fact]
    public void Emit_InstanceMethod_HasExportAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "doSomething",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"doSomething\")]", result);
        Assert.Contains("void DoSomething();", result);
    }

    // --- Static method ---

    [Fact]
    public void Emit_ClassMethod_HasStaticAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "sharedInstance",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = false
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Static]", result);
        Assert.Contains("[Export(\"sharedInstance\")]", result);
        Assert.Contains("MyClass SharedInstance();", result);
    }

    // --- Properties ---

    [Fact]
    public void Emit_ReadonlyProperty_HasGetterOnly()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "count",
                    Type = new ObjCTypeRef { Name = "NSInteger" },
                    IsReadonly = true,
                    GetterSelector = "count"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"count\")]", result);
        Assert.Contains("nint Count { get; }", result);
    }

    [Fact]
    public void Emit_ReadwriteProperty_HasGetterAndSetter()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "name",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = false,
                    GetterSelector = "name"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("get;", result);
        Assert.Contains("[Export(\"setName:\")] set;", result);
    }

    [Fact]
    public void Emit_ReadwritePropertyWithCustomSetter_EmitsSetterSelector()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "hidden",
                    Type = new ObjCTypeRef { Name = "BOOL" },
                    IsReadonly = false,
                    GetterSelector = "isHidden",
                    SetterSelector = "setHidden:"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"isHidden\")]", result);
        Assert.Contains("[Export(\"setHidden:\")] set;", result);
    }

    [Fact]
    public void Emit_ClassProperty_HasStaticAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "version",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = true,
                    IsClass = true,
                    GetterSelector = "version"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Static]", result);
        Assert.Contains("string Version { get; }", result);
    }

    // --- NullAllowed ---

    [Fact]
    public void Emit_NullableParam_HasNullAllowed()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "setName:",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "name",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true, Nullability = ObjCNullability.Nullable }
                    }]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[NullAllowed] string name", result);
    }

    [Fact]
    public void Emit_NullableReturn_HasReturnNullAllowed()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "getName",
                    ReturnType = new ObjCTypeRef { Name = "NSString", IsPointer = true, Nullability = ObjCNullability.Nullable },
                    IsInstanceMethod = true
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[return: NullAllowed]", result);
    }

    [Fact]
    public void Emit_NullableProperty_HasNullAllowed()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "title",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true, Nullability = ObjCNullability.Nullable },
                    IsReadonly = false,
                    GetterSelector = "title"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[NullAllowed]", result);
        Assert.Contains("get;", result);
        Assert.Contains("[Export(\"setTitle:\")] set;", result);
    }

    // --- NSError out parameter ---

    [Fact]
    public void Emit_NSErrorDoublePointer_EmitsOutParam()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "saveWithError:",
                    ReturnType = new ObjCTypeRef { Name = "BOOL" },
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "error",
                        Type = new ObjCTypeRef
                        {
                            Name = "NSError",
                            IsPointer = true,
                            PointeeType = new ObjCTypeRef { Name = "NSError", IsPointer = true }
                        }
                    }]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[NullAllowed] out NSError error", result);
    }

    // --- Block parameter ---

    [Fact]
    public void Emit_BlockParam_EmitsActionOrFunc()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "fetchWithCompletion:",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "completion",
                        Type = new ObjCTypeRef
                        {
                            Name = "block",
                            IsBlock = true,
                            BlockReturnType = new ObjCTypeRef { Name = "void" },
                            BlockParams = [new ObjCTypeRef { Name = "BOOL" }]
                        }
                    }]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("Action<bool> completion", result);
    }

    // --- Protocol ---

    [Fact]
    public void Emit_Protocol_HasProtocolAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "MyDelegate" }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Protocol]", result);
        Assert.Contains("[BaseType(typeof(NSObject))]", result);
        Assert.Contains("partial interface IMyDelegate", result);
    }

    [Fact]
    public void Emit_ProtocolRequiredMethod_HasAbstractAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "didFinish",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    IsOptional = false
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Abstract]", result);
        Assert.Contains("[Export(\"didFinish\")]", result);
    }

    [Fact]
    public void Emit_ProtocolOptionalMethod_NoAbstractAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "willStart",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    IsOptional = true
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.DoesNotContain("[Abstract]", result);
        Assert.Contains("[Export(\"willStart\")]", result);
    }

    [Fact]
    public void Emit_ProtocolWithInheritedProtocols_EmitsInheritanceList()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                InheritedProtocolNames = ["NSCoding", "NSCopying"]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("partial interface IMyDelegate : INSCoding, INSCopying", result);
    }

    [Fact]
    public void Emit_ProtocolRequiredProperty_HasAbstract()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "title",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = true,
                    IsOptional = false,
                    GetterSelector = "title"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Abstract]", result);
        Assert.Contains("string Title { get; }", result);
    }

    // --- Availability ---

    [Fact]
    public void Emit_IntroducedAvailability_EmitsAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Availability = [new ObjCAvailability
                {
                    Platform = "ios",
                    IntroducedVersion = "16.0"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Introduced(PlatformName.iOS, 16, 0)]", result);
    }

    [Fact]
    public void Emit_DeprecatedAvailability_EmitsAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Availability = [new ObjCAvailability
                {
                    Platform = "ios",
                    DeprecatedVersion = "15.0"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Deprecated(PlatformName.iOS, 15, 0)]", result);
    }

    [Fact]
    public void Emit_NonIosAvailability_Skipped()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Availability = [new ObjCAvailability
                {
                    Platform = "macos",
                    IntroducedVersion = "13.0"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.DoesNotContain("Introduced", result);
    }

    // --- Instancetype ---

    [Fact]
    public void Emit_InstancetypeInClass_MapsToClassName()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "sharedInstance",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = false
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("MyClass SharedInstance();", result);
    }

    [Fact]
    public void Emit_InstancetypeInProtocol_MapsToNSObject()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyProto",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "copy",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true,
                    IsOptional = false
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("NSObject Copy();", result);
    }

    // --- Selector to method name ---

    [Theory]
    [InlineData("doSomething", "DoSomething")]
    [InlineData("fooWithBar:baz:", "FooWithBar")]
    [InlineData("count", "Count")]
    [InlineData("setName:", "SetName")]
    [InlineData("a", "A")]
    public void SelectorToMethodName_ConvertsCorrectly(string selector, string expected)
    {
        Assert.Equal(expected, ApiDefinitionEmitter.SelectorToMethodName(selector));
    }

    // --- Empty module ---

    [Fact]
    public void Emit_EmptyModule_ProducesMinimalValidOutput()
    {
        var module = new ObjCModule { ModuleName = "Empty" };

        var result = EmitAndRead(module);
        Assert.Contains("using Foundation;", result);
        Assert.Contains("namespace TestNamespace", result);
        Assert.Contains("{", result);
        Assert.Contains("}", result);
        Assert.DoesNotContain("partial interface", result);
    }

    // --- Ordering ---

    [Fact]
    public void Emit_ProtocolsBeforeClasses()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "MyProto" }],
            Classes = [new ObjCClassDecl { Name = "MyClass" }]
        };

        var result = EmitAndRead(module);
        var protoIndex = result.IndexOf("IMyProto", StringComparison.Ordinal);
        var classIndex = result.IndexOf("partial interface MyClass", StringComparison.Ordinal);
        Assert.True(protoIndex < classIndex, "Protocols should be emitted before classes");
    }

    // --- Namespace ---

    [Fact]
    public void Emit_UsesResolvedNamespace()
    {
        var module = new ObjCModule { ModuleName = "Test" };
        var result = EmitAndRead(module, "MyCompany.iOS.Bindings");
        Assert.Contains("namespace MyCompany.iOS.Bindings", result);
    }

    // --- Method availability ---

    [Fact]
    public void Emit_MethodAvailability_EmitsAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "newApi",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    Availability = [new ObjCAvailability
                    {
                        Platform = "ios",
                        IntroducedVersion = "17.0"
                    }]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Introduced(PlatformName.iOS, 17, 0)]", result);
    }
}
