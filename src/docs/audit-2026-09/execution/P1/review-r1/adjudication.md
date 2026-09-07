# P1 round 1 adjudication — lead Astra

Full independent reports and exact sessions are in claude/ and grok/. Both source snapshots matched. Accepted High requires affected validation and resume of both original sessions.

| Candidate provenance | Disposition and evidence |
| --- | --- |
| Both High: Nuke “not a test failure” matched generic verdict substring | Accepted High. Exact complete exhausted-launch exception fixture makes real run_tests fail before repair (ci-golden-before-r2.log). Match actual app TEST FAILURE: delimiter; retain failure/crash/loader/start markers. Test nonzero and TimeoutExpired, cross-stream marker dominance. ci-after-r2.log:7 tests pass. |
| Claude M1 stale missing_input_cause contract and compile_failed→generate_failed relabel | Accepted documentation defect, semantics intentionally retained: failed generator cannot publish. Corrected docstring and compatibility note; diagnostic compile status remains recorded independently. |
| Claude M2 tests not wired | Accepted for main CI/release; both test jobs now run permanent Python acceptance tests before nuke test. Corpus has no CI and its existing receipt tests are manually invoked; keep that established local harness convention rather than invent cross-repo CI. Explicit commands retained in results. |
| Grok Low missing-product mismatch explanation | Accepted. Wording now refers to requested product names, truthful both for empty and partial subsets. |
| Claude L1 full artifact ownership inventory IO/size | Retained intentional provenance cost. Results documents inventory may include generated native artifacts; no recursive filtering that silently drops ownership evidence. |
| Claude L2 / Grok dropped normalized twins | Preexisting and absent current candidates, but accepted bounded completeness correction under program’s every-requested-primary requirement. Missing work computed from selected product nodes; collapsed requested twin gets input_provenance_unknown, unauthorized publication and nonzero aggregate. Five corpus tests pass including twin control; dependency graph twin exclusion remains. |
| Claude L3 / Grok dropped aggregate-status precedence | Intentional conservative nonzero result with exact per-product dispositions. Existing ratchet manual-trace bucket is honest; no threshold/baseline reclassification to hide failure. |
| Claude L4 first ownership set overwritten | Explicit limit: output ownership set is final publishing attempt; uniquely retained prior attempt logs remain diagnostic. No claim prior artifacts preserved as final products. |
| Claude L5 accumulating attempt logs | Intentional failure retention. No automatic purge during audit; housekeeping policy outside program. |
| Claude L6 timeout/hang no retry | Accepted program behavior: only established pretest launcher failure may retry, product timeout remains failure. |
| Both sanitized fixture gap; Grok TimeoutExpired gap | Merged into High, both exit paths now exercise complete source-derived exhaust text. |
| Grok SKIP marker mismatch | Dismissed: TEST/output evidence dominates; no demonstrated retry hole. |
| Claude tier removal, latest generate.log compatibility, stdout/stderr normalization, wanted fallback, additive consumers, corpus test fidelity, copyright convention | Reviewed dismissals accepted for source reasons in complete report. No unsupported clean native claim. |

Natural refusal control is classification replay, not fresh compiler/native evidence. No baseline changed for this package. Reviews do not qualify missing integrated runtime/corpus experiments.
