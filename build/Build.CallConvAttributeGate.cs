// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.CallConvAttributeGate.cs — "no redundant [UnmanagedCallConv]" ship gate for
// `nuke binding-tests --compile-only`.
//
// The Mono AOT compiler (the default runtime of a .NET-for-iOS device build, and of the arm64
// simulator's AOT) decodes the [UnmanagedCallConv] attribute of every P/Invoke that carries one,
// allocating as it goes, and it cannot run a garbage collection while it compiles. An app that holds a
// few tens of thousands of attributed P/Invokes exhausts the nursery and the compiler aborts. Most of
// a binding's P/Invokes call @_cdecl wrappers, native thunks or native bridges, all with the C
// convention, which is the platform default on every Apple target: an attribute naming it changes
// nothing about the call. The generator therefore writes the attribute only for the Swift convention,
// and this gate keeps it that way.
//
// It reads the compiled assemblies, not the C# text, so it counts exactly what the AOT compiler
// decodes (the inner declaration a source-generated LibraryImport forwards its attribute to), and it
// covers the generated bindings, the Apple supplement and the runtime: everything SwiftBindings adds
// to a consumer app. It fails on any attribute naming CallConvCdecl or naming no convention at all,
// and reports, per assembly, the P/Invoke count, the attributed count and the Swift-convention count.
// An assembly where it read no Swift-convention attribute also fails: a reader that cannot see the
// attribute would otherwise pass the redundancy check without reading anything.
//
// Fail-closed and unconditional, with no `--permissive` arm: a policy, not a baseline.

using System;
using System.IO;
using System.Linq;
using Serilog;
using SwiftBindings.TransitionGate;

partial class Build
{
    /// <summary>The compiled assemblies the call-convention attribute gate reads, from the compile check's output.</summary>
    static readonly string[] CallConvAttributeGateAssemblies = { "CompileCheck.dll", "SwiftBindings.Apple.dll", RuntimeAssemblyName };

    /// <summary>
    /// Asserts that no P/Invoke in the generated bindings, the Apple supplement or the runtime carries a
    /// <c>CallConvCdecl</c> <c>[UnmanagedCallConv]</c>, and reports the attributed counts. Invoked from the
    /// --compile-only path once the compile check has built the assemblies.
    /// </summary>
    void RunCallConvAttributeGate()
    {
        Log.Information("=========================================");
        Log.Information(" Call-convention attribute gate");
        Log.Information("=========================================");

        var failures = new System.Collections.Generic.List<string>();
        foreach (var name in CallConvAttributeGateAssemblies)
        {
            var path = CompileCheckOutputDir / name;
            if (!File.Exists(path))
                throw new Exception($"Call-convention attribute gate: {path} not found; the compile check did not produce it.");

            var census = CallConvAttributeCensus.Take(path);
            Log.Information("  {Assembly}: {PInvokes} P/Invoke(s), {Attributed} with [UnmanagedCallConv], {Swift} Swift-convention, {Redundant} redundant",
                census.Assembly, census.PInvokes, census.Attributed, census.Swift, census.Redundant.Length);
            foreach (var redundant in census.Redundant.Take(20))
                Log.Error("    ✗ {Site}", redundant);
            if (census.Redundant.Length > 20)
                Log.Error("    … and {More} more", census.Redundant.Length - 20);
            foreach (var error in census.Errors)
                Log.Error("    ✗ {Error}", error);

            if (!census.Redundant.IsEmpty)
                failures.Add($"{name}: {census.Redundant.Length} P/Invoke(s) carry a CallConvCdecl or empty [UnmanagedCallConv]");
            if (!census.Errors.IsEmpty)
                failures.Add($"{name}: {census.Errors.Length} attribute(s) did not decode");
            if (census.Swift == 0)
                failures.Add($"{name}: no Swift-convention [UnmanagedCallConv] read, so the census read nothing");
        }

        if (failures.Count > 0)
            throw new Exception(
                "Call-convention attribute gate: " + string.Join(" | ", failures) +
                ". The C convention is the platform default; write [UnmanagedCallConv] only for a non-default " +
                "convention (PInvokeEmitHelper.CallConvAttributeLine).");
        Log.Information("  ✓ no redundant [UnmanagedCallConv] attribute");
    }
}
