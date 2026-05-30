// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Numerics;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Tests for the Swift `simd` module projection onto `System.Numerics`.
///
/// Projection map (see `src/Swift.Runtime/src/Swift/SimdDatabase.xml` and
/// `BoundGenericSimdAliases` in `TypeDatabaseExtensions.cs`):
/// <list type="bullet">
///   <item><c>SIMD2&lt;Float&gt;</c> / <c>simd_float2</c> → <see cref="Vector2"/> (8 bytes)</item>
///   <item><c>SIMD3&lt;Float&gt;</c> / <c>simd_float3</c> → <see cref="Vector3"/> (16-byte Swift layout, 4th lane padding)</item>
///   <item><c>SIMD4&lt;Float&gt;</c> / <c>simd_float4</c> → <see cref="Vector4"/> (16 bytes)</item>
///   <item><c>simd_float4x4</c> → <see cref="Matrix4x4"/> (64 bytes)</item>
/// </list>
/// `simd_float3x3` has no System.Numerics equivalent (Matrix3x2 is 3×2, 24 bytes,
/// not 3×3, 48 bytes) and intentionally falls through to the [OpaqueSwiftType] +
/// SB0001 path.
/// </summary>
public class SimdProjectionTests : TestBase
{
    public SimdProjectionTests(TestResults results) : base(results) { }

    private const float Tolerance = 1e-5f;

    private static void AssertFloatEqual(float expected, float actual, string message)
    {
        var delta = Math.Abs(expected - actual);
        if (delta > Tolerance)
        {
            throw new Exception($"{message}: expected {expected}, got {actual} (delta {delta})");
        }
    }

    #region simd_float2 → Vector2

    public void TestMakeFloat2ReturnsVector2()
    {
        Vector2 v = TestLibFunctions.MakeFloat2(3.0f, 4.0f);
        AssertFloatEqual(3.0f, v.X, "MakeFloat2.X");
        AssertFloatEqual(4.0f, v.Y, "MakeFloat2.Y");
    }

    public void TestEchoFloat2RoundTrips()
    {
        var input = new Vector2(1.5f, 2.5f);
        Vector2 output = TestLibFunctions.EchoFloat2(input);
        AssertFloatEqual(1.5f, output.X, "EchoFloat2.X survives round-trip");
        AssertFloatEqual(2.5f, output.Y, "EchoFloat2.Y survives round-trip");
    }

    public void TestSumFloat2PreservesFieldValues()
    {
        var input = new Vector2(10.0f, 20.0f);
        float sum = TestLibFunctions.SumFloat2(input);
        AssertFloatEqual(30.0f, sum, "SumFloat2 reads both lanes");
    }

    #endregion

    #region simd_float3 → Vector3

    public void TestMakeFloat3ReturnsVector3()
    {
        Vector3 v = TestLibFunctions.MakeFloat3(1.0f, 2.0f, 3.0f);
        AssertFloatEqual(1.0f, v.X, "MakeFloat3.X");
        AssertFloatEqual(2.0f, v.Y, "MakeFloat3.Y");
        AssertFloatEqual(3.0f, v.Z, "MakeFloat3.Z");
    }

    public void TestEchoFloat3RoundTrips()
    {
        var input = new Vector3(7.0f, 8.0f, 9.0f);
        Vector3 output = TestLibFunctions.EchoFloat3(input);
        AssertFloatEqual(7.0f, output.X, "EchoFloat3.X survives round-trip");
        AssertFloatEqual(8.0f, output.Y, "EchoFloat3.Y survives round-trip");
        AssertFloatEqual(9.0f, output.Z, "EchoFloat3.Z survives round-trip");
    }

    public void TestSumFloat3PreservesFieldValues()
    {
        var input = new Vector3(1.0f, 2.0f, 3.0f);
        float sum = TestLibFunctions.SumFloat3(input);
        AssertFloatEqual(6.0f, sum, "SumFloat3 reads 3 active lanes");
    }

    #endregion

    #region simd_float4 → Vector4

    public void TestMakeFloat4ReturnsVector4()
    {
        Vector4 v = TestLibFunctions.MakeFloat4(1.0f, 2.0f, 3.0f, 4.0f);
        AssertFloatEqual(1.0f, v.X, "MakeFloat4.X");
        AssertFloatEqual(2.0f, v.Y, "MakeFloat4.Y");
        AssertFloatEqual(3.0f, v.Z, "MakeFloat4.Z");
        AssertFloatEqual(4.0f, v.W, "MakeFloat4.W");
    }

    public void TestEchoFloat4RoundTrips()
    {
        var input = new Vector4(10.0f, 20.0f, 30.0f, 40.0f);
        Vector4 output = TestLibFunctions.EchoFloat4(input);
        AssertFloatEqual(10.0f, output.X, "EchoFloat4.X survives round-trip");
        AssertFloatEqual(20.0f, output.Y, "EchoFloat4.Y survives round-trip");
        AssertFloatEqual(30.0f, output.Z, "EchoFloat4.Z survives round-trip");
        AssertFloatEqual(40.0f, output.W, "EchoFloat4.W survives round-trip");
    }

    public void TestSumFloat4PreservesFieldValues()
    {
        var input = new Vector4(1.0f, 2.0f, 3.0f, 4.0f);
        float sum = TestLibFunctions.SumFloat4(input);
        AssertFloatEqual(10.0f, sum, "SumFloat4 reads all 4 lanes");
    }

    #endregion

    #region simd_float4x4 → Matrix4x4

    public void TestIdentityFloat4x4IsMatrix4x4Identity()
    {
        Matrix4x4 m = TestLibFunctions.GetIdentityFloat4x4();
        // Swift simd matrices are column-major. System.Numerics Matrix4x4 is row-major.
        // The identity matrix is the same in both conventions; assert its diagonal.
        AssertFloatEqual(1.0f, m.M11, "Identity M11");
        AssertFloatEqual(1.0f, m.M22, "Identity M22");
        AssertFloatEqual(1.0f, m.M33, "Identity M33");
        AssertFloatEqual(1.0f, m.M44, "Identity M44");
        AssertFloatEqual(0.0f, m.M12, "Identity M12");
        AssertFloatEqual(0.0f, m.M21, "Identity M21");
    }

    public void TestEchoFloat4x4RoundTrips()
    {
        // Construct a distinctive diagonal matrix (diagonal is invariant under transpose).
        var input = Matrix4x4.Identity;
        input.M11 = 2.0f;
        input.M22 = 3.0f;
        input.M33 = 5.0f;
        input.M44 = 7.0f;
        Matrix4x4 output = TestLibFunctions.EchoFloat4x4(input);
        AssertFloatEqual(2.0f, output.M11, "EchoFloat4x4.M11 survives");
        AssertFloatEqual(3.0f, output.M22, "EchoFloat4x4.M22 survives");
        AssertFloatEqual(5.0f, output.M33, "EchoFloat4x4.M33 survives");
        AssertFloatEqual(7.0f, output.M44, "EchoFloat4x4.M44 survives");
    }

    public void TestDiagonalFloat4x4ReadsColumnMajorDiagonal()
    {
        // Build a 4x4 with a recognizable diagonal from 4 column vectors.
        // Swift stores simd_float4x4 column-major: col0 = (a, _, _, _).x  corresponds to M11.
        var col0 = new Vector4(1.0f, 0.0f, 0.0f, 0.0f); // col0.x = 1  → diagonal[0]
        var col1 = new Vector4(0.0f, 2.0f, 0.0f, 0.0f); // col1.y = 2  → diagonal[1]
        var col2 = new Vector4(0.0f, 0.0f, 3.0f, 0.0f); // col2.z = 3  → diagonal[2]
        var col3 = new Vector4(0.0f, 0.0f, 0.0f, 4.0f); // col3.w = 4  → diagonal[3]
        var m = TestLibFunctions.MakeFloat4x4(col0, col1, col2, col3);

        Vector4 diag = TestLibFunctions.DiagonalFloat4x4(m);
        AssertFloatEqual(1.0f, diag.X, "Diagonal[0]");
        AssertFloatEqual(2.0f, diag.Y, "Diagonal[1]");
        AssertFloatEqual(3.0f, diag.Z, "Diagonal[2]");
        AssertFloatEqual(4.0f, diag.W, "Diagonal[3]");
    }

    #endregion

    #region Stored properties exposed as System.Numerics types

    public void TestTransformHolderConstructsWithSimdProperties()
    {
        var position = new Vector3(1.0f, 2.0f, 3.0f);
        var color = new Vector4(0.25f, 0.5f, 0.75f, 1.0f);
        using var holder = new TransformHolder(position, color);
        AssertNotNull(holder, "TransformHolder constructs with SIMD properties");
    }

    public void TestTransformHolderPositionPropertyRoundTrips()
    {
        var position = new Vector3(1.0f, 2.0f, 3.0f);
        var color = new Vector4(0.0f, 0.0f, 0.0f, 1.0f);
        using var holder = new TransformHolder(position, color);

        Vector3 readBack = holder.Position;
        AssertFloatEqual(1.0f, readBack.X, "Position.X round-trips through property getter");
        AssertFloatEqual(2.0f, readBack.Y, "Position.Y round-trips through property getter");
        AssertFloatEqual(3.0f, readBack.Z, "Position.Z round-trips through property getter");
    }

    public void TestTransformHolderColorPropertyRoundTrips()
    {
        var position = new Vector3(0.0f, 0.0f, 0.0f);
        var color = new Vector4(0.1f, 0.2f, 0.3f, 0.4f);
        using var holder = new TransformHolder(position, color);

        Vector4 readBack = holder.Color;
        AssertFloatEqual(0.1f, readBack.X, "Color.X round-trips");
        AssertFloatEqual(0.2f, readBack.Y, "Color.Y round-trips");
        AssertFloatEqual(0.3f, readBack.Z, "Color.Z round-trips");
        AssertFloatEqual(0.4f, readBack.W, "Color.W round-trips");
    }

    #endregion

    #region Async + SIMD (WrapperEmitter.Async.cs wedge)

    // Async @_cdecl with SIMD bound-generic params: PInvokeEmitter routes
    // Swift.SIMD3/4<Float> through CdeclFrozenStruct (IntPtr), so the async
    // heap-buffer path must emit the matching `{name}Ptr` local rather than
    // skipping all bound-generics. Compile-time regression for this surface
    // lives in the generator suite; these tests pin the runtime ABI.

    // Generator drops Swift's "async" prefix and adds the C# "Async" suffix;
    // `asyncSumFloat3` thus emits as `SumFloat3Async` on the `Functions` class.

    public async Task TestAsyncSumFloat3PreservesFieldValues()
    {
        var input = new Vector3(1.0f, 2.0f, 3.5f);
        float sum = await WithTimeout(
            Functions.SumFloat3Async(input),
            DefaultAsyncTimeout);
        AssertFloatEqual(6.5f, sum, "AsyncSumFloat3 sums all three lanes");
    }

    public async Task TestAsyncEchoFloat3RoundTrips()
    {
        var input = new Vector3(0.25f, 0.5f, 0.75f);
        Vector3 output = await WithTimeout(
            Functions.EchoFloat3Async(input),
            DefaultAsyncTimeout);
        AssertFloatEqual(0.25f, output.X, "AsyncEchoFloat3.X survives round-trip");
        AssertFloatEqual(0.5f, output.Y, "AsyncEchoFloat3.Y survives round-trip");
        AssertFloatEqual(0.75f, output.Z, "AsyncEchoFloat3.Z survives round-trip");
    }

    public async Task TestAsyncEchoFloat4RoundTrips()
    {
        var input = new Vector4(1.0f, 2.0f, 3.0f, 4.0f);
        Vector4 output = await WithTimeout(
            Functions.EchoFloat4Async(input),
            DefaultAsyncTimeout);
        AssertFloatEqual(1.0f, output.X, "AsyncEchoFloat4.X survives round-trip");
        AssertFloatEqual(2.0f, output.Y, "AsyncEchoFloat4.Y survives round-trip");
        AssertFloatEqual(3.0f, output.Z, "AsyncEchoFloat4.Z survives round-trip");
        AssertFloatEqual(4.0f, output.W, "AsyncEchoFloat4.W survives round-trip");
    }

    public async Task TestAsyncEchoFloat4x4RoundTrips()
    {
        var input = Matrix4x4.Identity;
        Matrix4x4 output = await WithTimeout(
            Functions.EchoFloat4x4Async(input),
            DefaultAsyncTimeout);
        AssertFloatEqual(1.0f, output.M11, "AsyncEchoFloat4x4.M11 survives round-trip");
        AssertFloatEqual(1.0f, output.M22, "AsyncEchoFloat4x4.M22 survives round-trip");
        AssertFloatEqual(1.0f, output.M33, "AsyncEchoFloat4x4.M33 survives round-trip");
        AssertFloatEqual(1.0f, output.M44, "AsyncEchoFloat4x4.M44 survives round-trip");
    }

    #endregion

    #region Multi-SIMD constructor (RealityKit.Transform shape — §5a)

    // SimdDefaultCtorStruct is a resilient (non-@frozen) struct → ClassWithOpaquePayload, the same
    // C# shape the generator gives RealityKit.Transform. Its initializer takes three SIMD parameters
    // by value (a mix of bound-generic SIMD3<Float> → Vector3 and the C-imported simd_quatf →
    // Quaternion). Without the indirect (pointer) wrapper these route by register through CallConvSwift
    // and Mono's JIT throws InvalidProgramException at construction. Reaching the assertions at all
    // proves the @_cdecl wrapper is in place and the SIMD params crossed correctly.

    public void TestSimdMultiParamCtorConstructsAndRoundTrips()
    {
        var scale = new Vector3(2.0f, 3.0f, 4.0f);
        var rotation = new Quaternion(0.0f, 0.0f, 0.0f, 1.0f); // identity
        var translation = new Vector3(5.0f, 6.0f, 7.0f);

        using var t = new SimdDefaultCtorStruct(scale, rotation, translation);

        Vector3 s = t.Scale;
        AssertFloatEqual(2.0f, s.X, "Scale.X survives the multi-SIMD ctor");
        AssertFloatEqual(3.0f, s.Y, "Scale.Y survives the multi-SIMD ctor");
        AssertFloatEqual(4.0f, s.Z, "Scale.Z survives the multi-SIMD ctor");

        Vector3 tr = t.Translation;
        AssertFloatEqual(5.0f, tr.X, "Translation.X survives the multi-SIMD ctor");
        AssertFloatEqual(6.0f, tr.Y, "Translation.Y survives the multi-SIMD ctor");
        AssertFloatEqual(7.0f, tr.Z, "Translation.Z survives the multi-SIMD ctor");

        Quaternion r = t.Rotation;
        AssertFloatEqual(1.0f, r.W, "Rotation.W (identity) survives the multi-SIMD ctor");
    }

    // Single-SIMD-param control: init(basis:) already wrapped correctly before the fix. Confirms the
    // indirect path is sound for SIMD ctor params and isolates the multi-param case as the regression.
    public void TestSimdSingleParamCtorConstructs()
    {
        var basis = Matrix4x4.Identity;
        basis.M11 = 2.0f;
        basis.M22 = 3.0f;
        basis.M33 = 4.0f;

        using var t = new SimdDefaultCtorStruct(basis);
        AssertNotNull(t, "init(basis:) constructs via the single-SIMD indirect wrapper");
    }

    #endregion
}
