// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class MonoJitRiskDetectorTests
{
    #region ClosureParameter Detection

    [Fact]
    public void AnalyzeMethod_ClosureParam_DetectsClosureRisk()
    {
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty) });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ClosureParameter));
    }

    [Fact]
    public void AnalyzeMethod_OptionalClosureParam_DetectsClosureRisk()
    {
        var optionalClosure = new NamedTypeSpec("Swift.Optional",
            new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty));
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { optionalClosure });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ClosureParameter));
    }

    [Fact]
    public void AnalyzeMethod_ClosureWithArgs_DetectsClosureRisk()
    {
        var closure = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"));
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { closure });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ClosureParameter));
    }

    [Fact]
    public void AnalyzeMethod_ConventionCClosure_NotDetected()
    {
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var attr = new TypeSpecAttribute("convention");
        attr.Parameters.Add("c");
        closure.Attributes.Add(attr);

        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { closure });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.False(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ClosureParameter));
    }

    [Fact]
    public void AnalyzeMethod_ConventionBlockClosure_DetectsClosureRisk()
    {
        // @convention(block) closures still use Swift ABI, not safe
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var attr = new TypeSpecAttribute("convention");
        attr.Parameters.Add("block");
        closure.Attributes.Add(attr);

        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { closure });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ClosureParameter));
    }

    [Fact]
    public void AnalyzeMethod_OptionalConventionCClosure_NotDetected()
    {
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var attr = new TypeSpecAttribute("convention");
        attr.Parameters.Add("c");
        closure.Attributes.Add(attr);

        var optionalClosure = new NamedTypeSpec("Swift.Optional", closure);
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { optionalClosure });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.False(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ClosureParameter));
    }

    #endregion

    #region ExistentialParameter Detection

    [Fact]
    public void AnalyzeMethod_ProtocolListParam_DetectsExistentialRisk()
    {
        var protocolList = new ProtocolListTypeSpec(
            new[] { new NamedTypeSpec("TestModule.MyProtocol") });
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { protocolList });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ExistentialParameter));
    }

    [Fact]
    public void AnalyzeMethod_SingleProtocolExistentialIsAny_DetectsExistentialRisk()
    {
        var namedType = new NamedTypeSpec("TestModule.Describable") { IsAny = true };
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { namedType });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ExistentialParameter));
    }

    [Fact]
    public void AnalyzeMethod_EmptyProtocolList_DetectsExistentialRisk()
    {
        // 'any' with zero protocols (Any type)
        var protocolList = new ProtocolListTypeSpec();
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { protocolList });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ExistentialParameter));
    }

    [Fact]
    public void AnalyzeMethod_MultiProtocolComposition_DetectsExistentialRisk()
    {
        var protocolList = new ProtocolListTypeSpec(
            new[] { new NamedTypeSpec("TestModule.Describable"), new NamedTypeSpec("TestModule.Identifiable") });
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { protocolList });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ExistentialParameter));
    }

    [Fact]
    public void AnalyzeMethod_OptionalExistentialParam_DetectsExistentialRisk()
    {
        // Optional<any Protocol> should still be detected
        var protocolList = new ProtocolListTypeSpec(
            new[] { new NamedTypeSpec("TestModule.MyProtocol") });
        var optionalExistential = new NamedTypeSpec("Swift.Optional", protocolList);
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { optionalExistential });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ExistentialParameter));
    }

    [Fact]
    public void AnalyzeMethod_OptionalIsAnyParam_DetectsExistentialRisk()
    {
        // Optional<any SingleProtocol> via NamedTypeSpec with IsAny
        var anyProto = new NamedTypeSpec("TestModule.Describable") { IsAny = true };
        var optionalExistential = new NamedTypeSpec("Swift.Optional", anyProto);
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { optionalExistential });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ExistentialParameter));
    }

    #endregion

    #region SwiftStringReturn Detection

    [Fact]
    public void AnalyzeMethod_SwiftStringReturn_DetectsStringRisk()
    {
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.String"),
            paramTypes: new TypeSpec[] { new NamedTypeSpec("Swift.Int") });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.SwiftStringReturn));
    }

    [Fact]
    public void AnalyzeMethod_OptionalSwiftStringReturn_DetectsStringRisk()
    {
        // Optional<Swift.String> return should also be detected
        var optionalString = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.String"));
        var method = CreateMethod(
            returnType: optionalString,
            paramTypes: new TypeSpec[] { new NamedTypeSpec("Swift.Int") });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.SwiftStringReturn));
    }

    [Fact]
    public void AnalyzeMethod_SwiftStringParam_DoesNotDetectStringReturn()
    {
        // String as a parameter is NOT a SwiftStringReturn risk
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { new NamedTypeSpec("Swift.String") });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.False(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.SwiftStringReturn));
    }

    #endregion

    #region Combined Risk Detection

    [Fact]
    public void AnalyzeMethod_ClosureAndSwiftStringReturn_DetectsBothRisks()
    {
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.String"),
            paramTypes: new TypeSpec[] { new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty) });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ClosureParameter));
        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.SwiftStringReturn));
    }

    [Fact]
    public void AnalyzeMethod_AllThreeRisks_DetectsAll()
    {
        var protocolList = new ProtocolListTypeSpec(
            new[] { new NamedTypeSpec("TestModule.MyProtocol") });
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.String"),
            paramTypes: new TypeSpec[] { closure, protocolList });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ClosureParameter));
        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ExistentialParameter));
        Assert.True(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.SwiftStringReturn));
    }

    #endregion

    #region Negative Cases (No Risk)

    [Fact]
    public void AnalyzeMethod_PlainPrimitives_NoRisk()
    {
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { new NamedTypeSpec("Swift.Double"), new NamedTypeSpec("Swift.Bool") });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.Equal(MonoJitRiskDetector.MonoJitRisk.None, risk);
    }

    [Fact]
    public void AnalyzeMethod_NoParams_NoRisk()
    {
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: Array.Empty<TypeSpec>());

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.Equal(MonoJitRiskDetector.MonoJitRisk.None, risk);
    }

    [Fact]
    public void AnalyzeMethod_VoidReturn_NoRisk()
    {
        var method = CreateMethod(
            returnType: TupleTypeSpec.Empty,
            paramTypes: new TypeSpec[] { new NamedTypeSpec("Swift.Int") });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.Equal(MonoJitRiskDetector.MonoJitRisk.None, risk);
    }

    [Fact]
    public void AnalyzeMethod_NamedTypeNotString_NoStringRisk()
    {
        // A regular named type that has "String" in a different module is NOT SwiftString
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Foundation.Data"),
            paramTypes: Array.Empty<TypeSpec>());

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.False(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.SwiftStringReturn));
    }

    [Fact]
    public void AnalyzeMethod_NamedTypeNotExistential_NoExistentialRisk()
    {
        // Regular named type (not IsAny) should not be detected as existential
        var namedType = new NamedTypeSpec("TestModule.MyClass");
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { namedType });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.False(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ExistentialParameter));
    }

    [Fact]
    public void AnalyzeMethod_OptionalNonExistential_NoExistentialRisk()
    {
        // Optional<RegularType> should NOT be detected as existential
        var optionalInt = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: new TypeSpec[] { optionalInt });

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.False(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.ExistentialParameter));
    }

    [Fact]
    public void AnalyzeMethod_OptionalNonStringReturn_NoStringRisk()
    {
        // Optional<Swift.Int> return should NOT be detected as SwiftString risk
        var optionalInt = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));
        var method = CreateMethod(
            returnType: optionalInt,
            paramTypes: Array.Empty<TypeSpec>());

        var risk = MonoJitRiskDetector.AnalyzeMethod(method);

        Assert.False(risk.HasFlag(MonoJitRiskDetector.MonoJitRisk.SwiftStringReturn));
    }

    #endregion

    #region IsMonoJitRisk Convenience Method

    [Fact]
    public void IsMonoJitRisk_WithRisk_ReturnsTrue()
    {
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.String"),
            paramTypes: Array.Empty<TypeSpec>());

        Assert.True(MonoJitRiskDetector.IsMonoJitRisk(method));
    }

    [Fact]
    public void IsMonoJitRisk_WithoutRisk_ReturnsFalse()
    {
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: Array.Empty<TypeSpec>());

        Assert.False(MonoJitRiskDetector.IsMonoJitRisk(method));
    }

    #endregion

    #region ApplyRiskDetection

    [Fact]
    public void ApplyRiskDetection_RiskyMethod_SetsDetectedJitRisks()
    {
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.String"),
            paramTypes: Array.Empty<TypeSpec>());

        Assert.Equal(MonoJitRiskDetector.MonoJitRisk.None, method.DetectedJitRisks);

        MonoJitRiskDetector.ApplyRiskDetection(method);

        Assert.True(method.DetectedJitRisks.HasFlag(MonoJitRiskDetector.MonoJitRisk.SwiftStringReturn));
    }

    [Fact]
    public void ApplyRiskDetection_SafeMethod_LeavesNone()
    {
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: Array.Empty<TypeSpec>());

        MonoJitRiskDetector.ApplyRiskDetection(method);

        Assert.Equal(MonoJitRiskDetector.MonoJitRisk.None, method.DetectedJitRisks);
    }

    [Fact]
    public void ApplyRiskDetection_DoesNotSetUsesWrapperLibrary()
    {
        // Critical: detection must NOT affect P/Invoke routing
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.String"),
            paramTypes: new TypeSpec[] { new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty) });

        MonoJitRiskDetector.ApplyRiskDetection(method);

        Assert.False(method.UsesWrapperLibrary);
        Assert.NotEqual(MonoJitRiskDetector.MonoJitRisk.None, method.DetectedJitRisks);
    }

    [Fact]
    public void ApplyRiskDetection_DoesNotClearExistingUsesWrapperLibrary()
    {
        // UsesWrapperLibrary set by other emitters (ArraySlice, etc.) must not be disturbed
        var method = CreateMethod(
            returnType: new NamedTypeSpec("Swift.Int"),
            paramTypes: Array.Empty<TypeSpec>());
        method.UsesWrapperLibrary = true;

        MonoJitRiskDetector.ApplyRiskDetection(method);

        Assert.True(method.UsesWrapperLibrary);
    }

    #endregion

    #region Internal Helper Tests

    [Fact]
    public void IsSwiftStringType_SwiftString_ReturnsTrue()
    {
        Assert.True(MonoJitRiskDetector.IsSwiftStringType(new NamedTypeSpec("Swift.String")));
    }

    [Fact]
    public void IsSwiftStringType_OptionalSwiftString_ReturnsTrue()
    {
        var optionalString = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.String"));
        Assert.True(MonoJitRiskDetector.IsSwiftStringType(optionalString));
    }

    [Fact]
    public void IsSwiftStringType_PlainString_ReturnsFalse()
    {
        // "String" without module qualifier should NOT match
        Assert.False(MonoJitRiskDetector.IsSwiftStringType(new NamedTypeSpec("String")));
    }

    [Fact]
    public void IsSwiftStringType_NonString_ReturnsFalse()
    {
        Assert.False(MonoJitRiskDetector.IsSwiftStringType(new NamedTypeSpec("Swift.Int")));
    }

    [Fact]
    public void IsSwiftStringType_Closure_ReturnsFalse()
    {
        Assert.False(MonoJitRiskDetector.IsSwiftStringType(
            new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty)));
    }

    [Fact]
    public void IsSwiftStringType_OptionalNonString_ReturnsFalse()
    {
        var optionalInt = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));
        Assert.False(MonoJitRiskDetector.IsSwiftStringType(optionalInt));
    }

    [Fact]
    public void IsRiskyClosureType_EscapingClosure_ReturnsTrue()
    {
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.True(MonoJitRiskDetector.IsRiskyClosureType(closure));
    }

    [Fact]
    public void IsRiskyClosureType_PlainClosure_ReturnsTrue()
    {
        // Closures without explicit attributes still use Swift convention
        Assert.True(MonoJitRiskDetector.IsRiskyClosureType(
            new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty)));
    }

    [Fact]
    public void IsRiskyClosureType_ConventionC_ReturnsFalse()
    {
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var attr = new TypeSpecAttribute("convention");
        attr.Parameters.Add("c");
        closure.Attributes.Add(attr);

        Assert.False(MonoJitRiskDetector.IsRiskyClosureType(closure));
    }

    [Fact]
    public void IsExistentialType_ProtocolList_ReturnsTrue()
    {
        Assert.True(MonoJitRiskDetector.IsExistentialType(new ProtocolListTypeSpec()));
    }

    [Fact]
    public void IsExistentialType_IsAnyNamed_ReturnsTrue()
    {
        Assert.True(MonoJitRiskDetector.IsExistentialType(
            new NamedTypeSpec("TestModule.Proto") { IsAny = true }));
    }

    [Fact]
    public void IsExistentialType_RegularNamed_ReturnsFalse()
    {
        Assert.False(MonoJitRiskDetector.IsExistentialType(
            new NamedTypeSpec("TestModule.MyClass")));
    }

    [Fact]
    public void IsExistentialType_OptionalProtocolList_ReturnsTrue()
    {
        var protocolList = new ProtocolListTypeSpec(
            new[] { new NamedTypeSpec("TestModule.MyProtocol") });
        var optional = new NamedTypeSpec("Swift.Optional", protocolList);

        Assert.True(MonoJitRiskDetector.IsExistentialType(optional));
    }

    [Fact]
    public void IsExistentialType_OptionalIsAny_ReturnsTrue()
    {
        var anyProto = new NamedTypeSpec("TestModule.Describable") { IsAny = true };
        var optional = new NamedTypeSpec("Swift.Optional", anyProto);

        Assert.True(MonoJitRiskDetector.IsExistentialType(optional));
    }

    [Fact]
    public void IsExistentialType_OptionalRegularType_ReturnsFalse()
    {
        var optional = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));

        Assert.False(MonoJitRiskDetector.IsExistentialType(optional));
    }

    #endregion

    #region Emission-Level Routing Tests (P3)

    [Fact]
    public void EmitMethod_RiskySignature_DllImportUsesModuleLib_WhenNoAsyncLibrary()
    {
        // When AsyncLibraryName is null, DllImport must use the module library
        // even though DetectedJitRisks is set
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Handler", moduleDecl);
        var method = CreateMethodDeclOnClass(
            name: "getName",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"),
            methodType: MethodType.Instance);

        MonoJitRiskDetector.ApplyRiskDetection(method);
        Assert.True(method.DetectedJitRisks.HasFlag(MonoJitRiskDetector.MonoJitRisk.SwiftStringReturn));
        Assert.False(method.UsesWrapperLibrary);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // LibraryImport should use module library, not wrapper
        Assert.Contains("[LibraryImport(\"/tmp/TestModule.dylib\"", csOutput);
    }

    [Fact]
    public void EmitMethod_RiskySignature_DllImportUsesModuleLib_WhenAsyncLibrarySet()
    {
        // Even with AsyncLibraryName set, risk detection alone must NOT reroute the DllImport.
        // Only UsesWrapperLibrary (set by wrapper generators) should trigger rerouting.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "/tmp/AsyncWrapper.dylib";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Handler", moduleDecl);
        var method = CreateMethodDeclOnClass(
            name: "getName",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"),
            methodType: MethodType.Instance);

        MonoJitRiskDetector.ApplyRiskDetection(method);
        Assert.True(method.DetectedJitRisks.HasFlag(MonoJitRiskDetector.MonoJitRisk.SwiftStringReturn));
        Assert.False(method.UsesWrapperLibrary);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // LibraryImport must use module library — NOT the async wrapper lib
        Assert.Contains("[LibraryImport(\"/tmp/TestModule.dylib\"", csOutput);
        Assert.DoesNotContain("AsyncWrapper", csOutput);
    }

    [Fact]
    public void EmitMethod_UsesWrapperLibraryExplicit_DllImportUsesWrapperLib()
    {
        // Contrast: when UsesWrapperLibrary IS set (by a real wrapper generator),
        // and AsyncLibraryName is set, DllImport should use the wrapper library.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "/tmp/AsyncWrapper.dylib";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Handler", moduleDecl);
        var method = CreateMethodDeclOnClass(
            name: "getName",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"),
            methodType: MethodType.Instance);

        // Simulate what a real wrapper generator does (e.g., ArraySlice normalization)
        method.UsesWrapperLibrary = true;

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("[LibraryImport(\"/tmp/AsyncWrapper.dylib\"", csOutput);
    }

    #endregion

    #region Test Helpers

    private static ModuleDecl CreateModuleDecl()
    {
        return CreateModuleDecl("TestModule");
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static MethodDecl CreateMethod(TypeSpec returnType, TypeSpec[] paramTypes)
    {
        var moduleDecl = CreateModuleDecl();
        var csSignature = new List<ArgumentDecl>
        {
            // First element is the return type
            new ArgumentDecl
            {
                SwiftTypeSpec = returnType,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl
            }
        };

        // Add parameters
        for (int i = 0; i < paramTypes.Length; i++)
        {
            csSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = paramTypes[i],
                Name = $"param{i}",
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl
            });
        }

        return new MethodDecl
        {
            Name = "testMethod",
            MangledName = "$s10TestModule10testMethodSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    /// <summary>
    /// Creates a method attached to a ClassDecl parent (needed for emission tests).
    /// </summary>
    private static MethodDecl CreateMethodDeclOnClass(
        string name,
        ClassDecl parentDecl,
        ModuleDecl moduleDecl,
        TypeSpec returnType,
        MethodType methodType)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule7HandlerC{name}SSyF",
            MethodType = methodType,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = string.Empty,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Handler"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Handler"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Handler"),
                MetadataAccessor = "$s10TestModule7HandlerCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static (string csOutput, string swiftOutput) EmitMethod(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    #endregion
}
