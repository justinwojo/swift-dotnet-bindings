# AnyType Baseline Audit (Before Foundation Fix)

Generated: 2026-02-17

## Summary

| Library | AnyTypeFallback Count | AnyType in .cs |
|---------|----------------------|----------------|
| BlinkID | 0 | 0 |
| Nuke | 0 | 4 |
| Lottie | 1 | 28 |
| Alamofire | 32 | — |

## BlinkID
No AnyTypeFallback entries.

## Nuke
No AnyTypeFallback entries. (4 references to AnyType type name in .cs are `using` alias, etc.)

## Lottie (1 AnyTypeFallback)

| Kind | Name | ContainingType | Details |
|------|------|----------------|---------|
| Property | animationLayer | Lottie.LottieAnimationLayer | `Swift.SwiftOptional<Swift.AnyType>` — root cause: `Optional<Any>` in bound generics |

## Alamofire (32 AnyTypeFallback)

| # | Kind | Name | ContainingType | Details | Root Cause Category |
|---|------|------|----------------|---------|---------------------|
| 1 | Property | defaultURLErrorOfflineCodes | Alamofire.OfflineRetrier | `SwiftSet<AnyType>` | Foundation value type (URLError.Code) |
| 2 | Property | encoding | Alamofire.StringResponseSerializer | `SwiftOptional<AnyType>` | Foundation value type (String.Encoding) |
| 3 | Property | response | Alamofire.DataResponse | `SwiftOptional<AnyType>` | Foundation class (URLResponse) |
| 4 | Property | metrics | Alamofire.DataResponse | `SwiftOptional<AnyType>` | Foundation class (URLSessionTaskMetrics) |
| 5 | Property | response | Alamofire.DownloadResponse | `SwiftOptional<AnyType>` | Foundation class (URLResponse) |
| 6 | Property | metrics | Alamofire.DownloadResponse | `SwiftOptional<AnyType>` | Foundation class (URLSessionTaskMetrics) |
| 7 | Property | failedStringEncoding | Alamofire.AFError | `SwiftOptional<AnyType>` | Foundation value type (String.Encoding) |
| 8 | Method | refresh | Alamofire.Authenticator | AnyType in generic arg | Foundation class (URLRequest→Credential) |
| 9 | Property | credential | Alamofire.AuthenticationInterceptor | `SwiftOptional<AnyType>` | Foundation class (URLCredential) |
| 10 | Property | response | Alamofire.WebSocketRequest.Completion | `SwiftOptional<AnyType>` | Foundation class (URLResponse) |
| 11 | Property | metrics | Alamofire.WebSocketRequest.Completion | `SwiftOptional<AnyType>` | Foundation class (URLSessionTaskMetrics) |
| 12 | Property | response | Alamofire.DataStreamRequest.Completion | `SwiftOptional<AnyType>` | Foundation class (URLResponse) |
| 13 | Property | metrics | Alamofire.DataStreamRequest.Completion | `SwiftOptional<AnyType>` | Foundation class (URLSessionTaskMetrics) |
| 14 | Method | adapt | Alamofire.RequestAdapter | AnyType in generic arg | Foundation class (URLRequest) |
| 15 | Method | adapt | Alamofire.RequestInterceptor | AnyType in generic arg | Foundation class (URLRequest) |
| 16 | Property | flags | Alamofire.NetworkReachabilityManager | `SwiftOptional<AnyType>` | Unknown (SCNetworkReachabilityFlags?) |
| 17 | Method | dataTask | Alamofire.CachedResponseHandler | AnyType in generic arg | Foundation class (URLSession/CachedURLResponse) |
| 18 | Property | defaultRetryableURLErrorCodes | Alamofire.RetryPolicy | `SwiftSet<AnyType>` | Foundation value type (URLError.Code) |
| 19 | Property | retryableURLErrorCodes | Alamofire.RetryPolicy | `SwiftSet<AnyType>` | Foundation value type (URLError.Code) |
| 20 | Property | credential | Alamofire.Request | `SwiftOptional<AnyType>` | Foundation class (URLCredential) |
| 21 | Property | response | Alamofire.Request | `SwiftOptional<AnyType>` | Foundation class (URLResponse) |
| 22 | Property | tasks | Alamofire.Request | `SwiftArray<AnyType>` | Foundation class (URLSessionTask) |
| 23 | Property | firstTask | Alamofire.Request | `SwiftOptional<AnyType>` | Foundation class (URLSessionTask) |
| 24 | Property | lastTask | Alamofire.Request | `SwiftOptional<AnyType>` | Foundation class (URLSessionTask) |
| 25 | Property | task | Alamofire.Request | `SwiftOptional<AnyType>` | Foundation class (URLSessionTask) |
| 26 | Property | allMetrics | Alamofire.Request | `SwiftArray<AnyType>` | Foundation class (URLSessionTaskMetrics) |
| 27 | Property | firstMetrics | Alamofire.Request | `SwiftOptional<AnyType>` | Foundation class (URLSessionTaskMetrics) |
| 28 | Property | lastMetrics | Alamofire.Request | `SwiftOptional<AnyType>` | Foundation class (URLSessionTaskMetrics) |
| 29 | Property | metrics | Alamofire.Request | `SwiftOptional<AnyType>` | Foundation class (URLSessionTaskMetrics) |
| 30 | Property | certificates | Alamofire.AlamofireExtension | `SwiftArray<AnyType>` | Security CF type (SecCertificate) |
| 31 | Property | publicKeys | Alamofire.AlamofireExtension | `SwiftArray<AnyType>` | Security CF type (SecKey) |
| 32 | Property | publicKey | Alamofire.AlamofireExtension | `SwiftOptional<AnyType>` | Security CF type (SecKey) |

### Root Cause Breakdown (Alamofire)
- **Foundation class types (URLResponse, URLSession*, URLCredential, URLRequest):** ~24 entries → Fixed by adding Foundation to AppleObjCFrameworkModules
- **Foundation value types (URLError.Code, String.Encoding):** ~5 entries → Remain as AnyType (correct behavior)
- **Security CF types (SecCertificate, SecKey):** 3 entries → Out of scope (CF types, not NSObject)
