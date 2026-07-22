# Session 5 — Signature/Generic/Naming Mechanisms

All 6 mechanisms shipped (commit `58991c42`): Factory, Eureka, SwiftUICharts, JTAppleCalendar, swift-argument-parser canaries green ("C# verification passed"); Macaw mechanism fixed — canary red only on the out-of-family pre-existing CS0029 operator defect.

Paired Codex+Grok review, 2 rounds. r1 fixes: name-axis parity via `PublicMethodNameContext.ForMethod` at 3 predictor sites; generic-param collision check moved after Async suffix (final-name check); `SwiftUI.Text` payload-semantics registration; inherited-property fallback gate; Arc `global::` sweep completed. r2: Grok clean; Codex Medium fixed — static witness name divergence now keeps the conformance (`static virtual` throwing default is compile-benign) instead of dropping it.

**Session 06 recordings:** (a) Macaw CS0029 — class-typed operator returns (`PInvoke_op_Addition`/`op_Subtraction`) assigned unwrapped nint at `Macaw.Types.Point.cs:117`, `Size.cs:148/156`; (b) getter-direction tuple lift (C#→Swift tuple returns through receivers) still passthrough — no canary evidence, deliberately not blind-fixed.

**Gates:** unit 15702/0 (floor 15702); BindingTests sim 3245/0/38 (baseline 3242); Analyzers 35/0; Runtime 730/0. New fixture `TupleParamCallback` proves per-element Date tuple conversion at runtime. Device leg recommended for the tuple fix (marshalling change; NativeAOT differs).
