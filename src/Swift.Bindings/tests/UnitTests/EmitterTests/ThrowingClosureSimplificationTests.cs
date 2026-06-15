// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ThrowingClosureSimplificationEmitter.
/// Verifies that:
/// 1. Void throwing closures produce Action overloads
/// 2. Non-void throwing closures produce Func overloads with unwrapped return
/// 3. Original method gets [EditorBrowsable(Never)]
/// 4. Overload body wraps in SwiftResult.FromSuccess
/// 5. Overload body catches SwiftErrorException → FromFailure
/// 6. Non-throwing closures → no overload
/// 7. Async+throwing → no overload (already Task-based)
/// 8. Method-level generics → no overload
/// </summary>
public class ThrowingClosureSimplificationTests
{
    private readonly TypeDatabase _typeDatabase;

    public ThrowingClosureSimplificationTests()
    {
        _typeDatabase = new TypeDatabase();
        _typeDatabase.LoadModuleDatabaseFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SwiftDatabase.xml")).Wait();
    }

    [Fact]
    public void VoidThrowingClosure_ProducesActionOverload()
    {
        // Method: func prepareDatabase(setup: (Database) throws -> Void)
        var method = CreateMethodWithThrowingClosure("prepareDatabase", "setup",
            hasClosureReturn: false, closureArgType: "TestModule.Database");
        var output = EmitOverload(method);

        Assert.Contains("Action<", output);
        Assert.Contains("SwiftResult", output);
        Assert.Contains("FromSuccess(Swift.SwiftVoid.Value)", output);
    }

    [Fact]
    public void NonVoidThrowingClosure_ProducesFuncOverload()
    {
        // Method: func transform(body: (Input) throws -> Bool)
        var method = CreateMethodWithThrowingClosure("transform", "body",
            hasClosureReturn: true, closureArgType: "Swift.Int",
            closureReturnType: "Swift.Bool");
        var output = EmitOverload(method);

        Assert.Contains("Func<", output);
        Assert.Contains("FromSuccess(", output);
    }

    [Fact]
    public void ThrowingOverload_CatchesSwiftErrorException()
    {
        var method = CreateMethodWithThrowingClosure("doWork", "action",
            hasClosureReturn: false);
        var output = EmitOverload(method);

        Assert.Contains("SwiftErrorException", output);
        Assert.Contains("FromFailure", output);
    }

    [Fact]
    public void OriginalMethod_ShouldSimplify_ReturnsTrue()
    {
        var method = CreateMethodWithThrowingClosure("doWork", "action",
            hasClosureReturn: false);
        var env = new MethodEnvironment(method, _typeDatabase);
        env.EmittedProjectedSignatures = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(ThrowingClosureSimplificationEmitter.ShouldSimplify(env));
    }

    [Fact]
    public void OverloadOutput_DoesNotContainEditorBrowsable()
    {
        // [EditorBrowsable(Never)] is emitted on the ORIGINAL method by WrapperEmitter,
        // not on the convenience overload.
        var method = CreateMethodWithThrowingClosure("doWork", "action",
            hasClosureReturn: false);
        var output = EmitOverload(method);

        Assert.DoesNotContain("EditorBrowsable", output);
    }

    [Fact]
    public void ShouldSimplify_ReturnsFalse_WhenDedupKeyAlreadyExists()
    {
        var method = CreateMethodWithThrowingClosure("doWork", "action",
            hasClosureReturn: false);
        var env = new MethodEnvironment(method, _typeDatabase);
        env.EmittedProjectedSignatures = new HashSet<string>(StringComparer.Ordinal);

        // First call succeeds (key not yet in set)
        Assert.True(ThrowingClosureSimplificationEmitter.ShouldSimplify(env));

        // Emit the overload using the same env (adds the key to its dedup set)
        var stringWriter = new StringWriter();
        var writer = new CSharpWriter(stringWriter);
        ThrowingClosureSimplificationEmitter.TryEmitOverload(writer, env);

        // Second call returns false — key already in set from TryEmitOverload
        Assert.False(ThrowingClosureSimplificationEmitter.ShouldSimplify(env));
    }

    [Fact]
    public void NonThrowingClosure_NoOverload()
    {
        var closureSpec = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = TupleTypeSpec.Empty,
            Throws = false,
        };

        var method = CreateMethodWithClosure("doWork", "action", closureSpec);
        var output = EmitOverload(method);

        Assert.Empty(output);
    }

    [Fact]
    public void AsyncThrowingClosure_NoOverload()
    {
        var closureSpec = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = TupleTypeSpec.Empty,
            Throws = true,
            IsAsync = true,
        };

        var method = CreateMethodWithClosure("doWork", "action", closureSpec);
        var output = EmitOverload(method);

        Assert.Empty(output);
    }

    [Fact]
    public void MethodLevelGenerics_NoOverload()
    {
        var method = CreateMethodWithThrowingClosure("doWork", "action",
            hasClosureReturn: false);
        // Add a method-level generic parameter
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("T", "T", new(), new())
        };
        var output = EmitOverload(method);

        Assert.Empty(output);
    }

    [Fact]
    public void Constructor_NoOverload()
    {
        var method = CreateMethodWithThrowingClosure("init", "action",
            hasClosureReturn: false);
        method.IsConstructor = true;
        var output = EmitOverload(method);

        Assert.Empty(output);
    }

    [Fact]
    public void SwiftErrorException_RuntimeType_Exists()
    {
        // Verify the runtime type is constructible
        var error = default(System.Runtime.InteropServices.Swift.SwiftError);
        var ex = new Swift.SwiftErrorException(error);
        Assert.NotNull(ex);
        Assert.Equal("A Swift error occurred.", ex.Message);
    }

    [Fact]
    public void WrapperLambda_UsesProjectedReturnType()
    {
        // Closure returns Swift.String which projects to "string".
        // The wrapper lambda's SwiftResult<T, SwiftError> must use the projected type,
        // not raw "SwiftString". Otherwise the delegate types disagree (P1 finding).
        var method = CreateMethodWithThrowingClosure("fetchName", "loader",
            hasClosureReturn: true, closureArgType: "Swift.Int",
            closureReturnType: "Swift.String");
        var output = EmitOverload(method);

        // The simplified overload should use Func<..., string> (projected)
        Assert.Contains("Func<", output);
        // The SwiftResult in the wrapper body must also use "string" (projected),
        // not "SwiftString" (raw). This ensures the lambda types are consistent.
        Assert.Contains("SwiftResult<string,", output);
        Assert.DoesNotContain("SwiftResult<SwiftString,", output);
    }

    #region Helper Methods

    private string EmitOverload(MethodDecl method)
    {
        var env = new MethodEnvironment(method, _typeDatabase);
        env.EmittedProjectedSignatures = new HashSet<string>(StringComparer.Ordinal);
        var stringWriter = new StringWriter();
        var writer = new CSharpWriter(stringWriter);
        ThrowingClosureSimplificationEmitter.TryEmitOverload(writer, env);
        return stringWriter.ToString();
    }

    private MethodDecl CreateMethodWithThrowingClosure(
        string methodName,
        string closureParamName,
        bool hasClosureReturn,
        string? closureArgType = null,
        string? closureReturnType = null)
    {
        var closureArgs = closureArgType != null
            ? (TypeSpec)new NamedTypeSpec(closureArgType)
            : TupleTypeSpec.Empty;

        var closureReturn = hasClosureReturn && closureReturnType != null
            ? new NamedTypeSpec(closureReturnType)
            : (TypeSpec)TupleTypeSpec.Empty;

        var closureSpec = new ClosureTypeSpec
        {
            Arguments = closureArgs,
            ReturnType = closureReturn,
            Throws = true,
        };

        return CreateMethodWithClosure(methodName, closureParamName, closureSpec);
    }

    private MethodDecl CreateMethodWithClosure(
        string methodName,
        string closureParamName,
        ClosureTypeSpec closureSpec)
    {
        var parentType = new ClassDecl
        {
            Name = "TestClass",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestClass"),
            MangledName = "sTestClass",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Conformances = new(),
            SuperclassNames = new(),
        };

        var method = new MethodDecl
        {
            Name = methodName,
            ParentDecl = parentType,
            ModuleDecl = null,
            MangledName = $"s{methodName}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            GenericParameters = new(),
            IsSynthesizedAccessor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (index 0): void
                new ArgumentDecl
                {
                    Name = "",
                    ParentDecl = null,
                    ModuleDecl = null,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                },
                // Closure parameter (index 1)
                new ArgumentDecl
                {
                    Name = closureParamName,
                    ParentDecl = null,
                    ModuleDecl = null,
                    SwiftTypeSpec = closureSpec,
                    PrivateName = closureParamName,
                    IsInOut = false,
                    IsGeneric = false,
                },
            },
        };

        parentType.Methods.Add(method);
        return method;
    }

    #endregion
}
