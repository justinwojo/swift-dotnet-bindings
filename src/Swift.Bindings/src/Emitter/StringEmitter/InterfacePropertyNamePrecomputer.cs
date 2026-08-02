// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Pre-emission pass that computes each protocol's actually-emitted C# interface
/// property-name set and publishes it into <see cref="ModuleEmissionContext"/>
/// before any protocol body is emitted. Downstream emitters (proxy explicit-interface
/// forwarders, BFS shadow detection, covariant-return forwarders) consume this set
/// to compute method projection keys with the same `propertyNames` collision context
/// the interface itself used.
///
/// Without this prepass the cache is populated lazily inside <c>ProtocolHandler.EmitProtocolImpl</c>,
/// so a same-module child protocol that consults an ancestor whose interface has not
/// been emitted yet falls back to the conservative "all declared properties"
/// approximation — reopening the over-inclusion edge for `new`-keyword detection
/// and proxy/forwarder dedup.
///
/// The filter logic here MUST mirror the inline computation in
/// <c>ProtocolHandler.EmitProtocolImpl</c> (search for <c>emittedCSharpPropertyNames</c>):
/// <list type="bullet">
///   <item>Skip duplicates (shared dup-detection across static and instance properties).</item>
///   <item>Static properties: include if they pass the dup check (mirror existing behavior — gate
///         result is not consulted in the inline post-emission set construction).</item>
///   <item>Instance properties whose gate result is <see cref="GateDisposition.Skip"/>: exclude.</item>
///   <item>Instance properties marked <see cref="GateDisposition.InterfaceOnly"/> WITHOUT
///         <see cref="SoftGateFlags.HasClosureProperty"/>: exclude (the inline path adds them
///         to <c>skippedPropertyNames</c> but not <c>closureSkippedPropertyNames</c>, so they
///         are filtered out of the final set).</item>
///   <item>Otherwise: include the projected C# property name.</item>
/// </list>
///
/// <see cref="MemberGateEvaluator.EvaluateProperty"/> is pure (no logging, no
/// ReportCollector side effects), so calling it twice — once here for prepass
/// caching, once during emission for the diagnostic record — is safe.
/// </summary>
internal static class InterfacePropertyNamePrecomputer
{
    public static void Precompute(ModuleDecl moduleDecl, ITypeDatabase typeDatabase, ModuleEmissionContext emissionContext)
    {
        var gateEvaluator = new MemberGateEvaluator(typeDatabase);
        foreach (var protocolDecl in moduleDecl.Protocols)
        {
            var protoQualifiedName = protocolDecl.SwiftTypeName?.ModuleQualifiedName
                                   ?? $"{protocolDecl.ModuleDecl?.Name ?? "Unknown"}.{protocolDecl.Name}";

            var emittedPropertyNames = new HashSet<string>();
            var seenKeys = new HashSet<string>();

            foreach (var propertyDecl in protocolDecl.Properties)
            {
                if (!seenKeys.Add(propertyDecl.Name))
                    continue;

                if (propertyDecl.IsStatic)
                {
                    emittedPropertyNames.Add(NameProvider.GetPropertyName(propertyDecl));
                    continue;
                }

                var gate = gateEvaluator.EvaluateProperty(propertyDecl, protocolDecl.ModuleDecl, protocolDecl);
                if (gate.IsSkipped)
                    continue;
                if (gate.IsInterfaceOnly && !gate.SoftFlags.HasFlag(SoftGateFlags.HasClosureProperty))
                    continue;

                emittedPropertyNames.Add(NameProvider.GetPropertyName(propertyDecl));
            }

            emissionContext.RecordInterfacePropertyNames(protoQualifiedName, emittedPropertyNames);
        }
    }
}
