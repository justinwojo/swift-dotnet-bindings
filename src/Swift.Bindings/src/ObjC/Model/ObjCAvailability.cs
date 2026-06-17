// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration.ObjC;

/// <summary>
/// One platform-availability record recovered from an Objective-C declaration's
/// <c>API_AVAILABLE</c> / <c>API_DEPRECATED</c> / <c>API_UNAVAILABLE</c> macros or a bare
/// <c>__attribute__((availability(...)))</c> attribute.
/// <para/>
/// The clang <c>-ast-dump=json</c> <c>AvailabilityAttr</c> node carries only
/// <c>{id, kind, range}</c> — it does NOT serialize the platform / introduced / deprecated
/// fields. The data is recovered (Finding 22, recovery option a2) by reading the consumer
/// header at the attribute's <c>range.begin</c> source offset (the macro-expansion site, NOT
/// the useless <c>spellingLoc</c> that points into the SDK's <c>AvailabilityInternal.h</c>) and
/// scanning the macro arguments. See <see cref="ObjCAvailabilityParser"/>.
/// <para/>
/// <see cref="Platform"/> is the .NET platform string the emitter writes
/// (<c>ios</c>/<c>macos</c>/<c>tvos</c>/<c>watchos</c>/<c>maccatalyst</c>/<c>visionos</c>),
/// already mapped from the Objective-C spelling so the emitter stays dumb.
/// </summary>
public sealed record ObjCAvailability
{
    /// <summary>.NET platform string: <c>ios</c>, <c>macos</c>, <c>tvos</c>, <c>watchos</c>,
    /// <c>maccatalyst</c>, or <c>visionos</c>.</summary>
    public required string Platform { get; init; }

    /// <summary>Introduced (first-available) version, e.g. <c>"15.0"</c>, or null.</summary>
    public string? IntroducedVersion { get; init; }

    /// <summary>Deprecated-since version, e.g. <c>"15.0"</c>, or null.</summary>
    public string? DeprecatedVersion { get; init; }

    /// <summary>Obsoleted (removed) version, or null.</summary>
    public string? ObsoletedVersion { get; init; }

    /// <summary>True when the declaration is marked unavailable on <see cref="Platform"/>.</summary>
    public bool IsUnavailable { get; init; }

    /// <summary>Optional deprecation/obsoletion message.</summary>
    public string? Message { get; init; }
}
