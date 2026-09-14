# Validation receipt

Runtime: Darwin/macOS; Xcode 26.3; .NET 10.

| Validation | Result |
| --- | --- |
| Focused Stage B implementation slice | Passed: 294 tests |
| Focused synthesized-accessor regression | Passed: 1 test |
| `./build.sh Test` after all review repairs | Passed in 1:45 |
| Withdrawal gate model | Passed: 65 assertions |
| Swift.Bindings unit suite | Passed: 19,026; skipped: 2; failed: 0 |
| Swift.Analyzers suite | Passed: 79; failed: 0 |
| Swift.Runtime library suite | Passed: 922; skipped: 1; failed: 0 |
| `./build.sh BindingTests --compile-only` after all review repairs | Passed in 5:07; generated C# and Swift compiled |
| Earlier post-implementation `PackGate` | Passed in 3:10 target time; 49 imports, 478 requirements, 10 slice/architecture pairs, six required slices |
| PackGate adversarial controls | Missing symbol rejected; wrong slice rejected; plist+archive slice deletion rejected |
| Earlier post-implementation BindingTests simulator run | Passed: 4,061; skipped: 32; failed: 0 |

The initial sandboxed focused `dotnet test` attempt failed before test execution
because MSBuild could not bind its local IPC pipe (`SocketException: Permission
denied`). The identical focused command was rerun outside the sandbox and passed;
this was an environment failure, not a product failure.

The final Nuke test run auto-raised the shared worktree floor from 19,025 to 19,026
because unrelated P3 tests are present. The committed audit patch stages only the
ten Stage B additions relative to HEAD: 18,970 to 18,980.
