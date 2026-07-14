# Data Pack — SB000x / SB1xxx Family (Generated + Analyzers)

Companion to full SWIFTBIND encyclopedia (`01-diagnostic-encyclopedia.md`).

| ID | Role | Severity shape | Notes |
|----|------|----------------|-------|
| **SB0001** | Mono JIT risk / non-blittable CallConvSwift / direct silgen | Warning (often NoWarn'd) | WrapperValidation / operators; consumer targets suppress |
| **SB0002** | Call site returns silent tombstone type | Diagnostic | SilentTombstoneRegistrar |
| **SB0003** | Non-dispatchable protocol member stub | Obsolete / warn | Proxy pragma disable; WitnessDispatch degrade |
| **SB0004** | Protocol/interface obsolete tag | Obsolete | Proxy implements obsolete interface |
| **SB0005** | Unsupported closure param **tombstone but reachable** | Obsolete | ClosureParamTombstoneEmitter; object? param |
| **SB0006** | **Compile-poison** produce-throw suppressed-proxy getter | Obsolete(**error:true**) | WrapperEmitter + property getter; G1-003 |
| **SB0007** | Reserved / rare | — | Low hit count in corpus |
| **SB1001** | Analyzer: dispose ISwiftObject | Warning/Info | Class vs buffer struct distinction |
| **SB1002** | Analyzer: retain cycle callback | Warning | Property receiver FN candidate (W9) |

### Poison spectrum (G1-relevant)

```
Honest skip (no public member)
  → SB0005 tombstone reachable (Obsolete soft)
  → SB0003 stub (dispatch dead)
  → SB0006 compile error if called (Obsolete error:true)
  → bare NotSupportedException without SB0006 (proxy InterfaceImpl lag)
```

**BindingTests:** 63 SuppressedProxyMemberDegraded including ~32 produce-throw → primarily **SB0006 path**.

### Default NoWarn (Sdk.props)

`SB0001;SB0002;SB0003;SB0004` suppressed in generated projects by default — **softens consumer visibility** of those diagnostics. SB0005/SB0006 still matter for poison API.
