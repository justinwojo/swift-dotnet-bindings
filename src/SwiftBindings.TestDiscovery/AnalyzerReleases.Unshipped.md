; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SBTD001 | SwiftBindings.TestDiscovery | Error | Discovered Test* method declared 'async void'; the discovery invoker cannot await it, so post-await assertions detach and the test falsely passes. Declare it 'async Task'.
SBTD002 | SwiftBindings.TestDiscovery | Error | Test* method on a TestBase class is non-public, static, or parameterized, so discovery silently skips it (a false green). Make it public/instance/parameterless or rename it.
SBTD003 | SwiftBindings.TestDiscovery | Error | Class named *Tests declares public Test* method(s) but does not derive TestBase, so discovery never runs its tests. Add ': TestBase'.
