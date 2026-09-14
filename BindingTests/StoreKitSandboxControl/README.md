# First-party StoreKit Sandbox control

This pure-Swift app is built and installed automatically by
`nuke binding-tests --enable-storekit-smoke` for iOS and
`nuke binding-tests --tvos --enable-storekit-smoke` for tvOS. It uses the exact
simulator and bundle identifier of the managed runtime test app. The managed app is installed only
after a fresh run-token receipt proves that `AppTransaction.shared`, exact product lookup,
`Transaction.currentEntitlements`, and `Transaction.all` all complete against Sandbox.

Set `STOREKIT_SANDBOX_PRODUCT_ID` (or pass `--storekit-sandbox-product-id`) to the exact App Store
Connect product for `com.swiftbindings.runtimetestsapp`, and sign each target simulator into a
Sandbox tester account. Missing state, a wrong product, a production/Xcode environment, an
unverified transaction, or any timeout is a non-pass.

Do not pass a `.storekit` path through `simctl`, `mlaunch`, `dotnet`, or this harness. Xcode scheme
launches activate StoreKit configuration files through a separate backend. Any Xcode-driven
control must remain a separate lane and cannot substitute for command-line Sandbox qualification.
