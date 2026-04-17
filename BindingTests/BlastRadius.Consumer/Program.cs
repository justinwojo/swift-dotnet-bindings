// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Session 5 / M9 blast-radius smoke test — "treatment" binary.
// Referenced dependencies: SwiftBindings.Runtime + SwiftBindings.Apple.
// Touches exactly one supplement type to force the supplement to link.

using Swift.Foundation;

// Static reference to the type ensures the linker cannot prune it away.
// GetTypeMetadata isn't invoked at runtime — just typeof keeps the metadata
// descriptors alive in the AOT image.
var languageType = typeof(Locale.Language);
Console.WriteLine($"BlastRadius.Consumer: referenced {languageType.FullName}.");
