// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Options bag for cross-cutting generator configuration.
    /// Currently carries PlatformInfo; will be extended as more options are threaded through.
    /// </summary>
    public sealed class BindingGeneratorOptions
    {
        /// <summary>
        /// The target Apple platform for this binding generation run.
        /// </summary>
        public required PlatformInfo PlatformInfo { get; init; }
    }
}
