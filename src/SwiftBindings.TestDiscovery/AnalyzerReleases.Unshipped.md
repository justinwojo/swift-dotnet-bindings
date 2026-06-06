; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SBTD001 | SwiftBindings.TestDiscovery | Error | Discovered Test* method declared 'async void'; the discovery invoker cannot await it, so post-await assertions detach and the test falsely passes. Declare it 'async Task'.
