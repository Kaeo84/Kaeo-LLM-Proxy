# Log Request Body Exactly As Received

## Understanding
The logged **Request Body** must be exactly the original request the client sent, with zero
transformations. The client's own `reasoning_effort` was missing from the logged body (it only
existed in the Upstream Request Body), and the body was pretty-printed — because every capture
site piped the body through `RedactSensitiveJsonFields`, which parsed and **re-serialized** the
whole document (indentation, key order, escaping), so the log was never byte-identical to the
wire bytes.

## Root Cause
`RedactSensitiveJsonFields` built a fresh `Utf8JsonWriter` tree and rewrote the entire document
(`Indented = true`), mutating formatting and (via the writer's default rules) any field the
serializer chose to drop/reorder. `RedactRequestBodyForLog` then used that as the "original" body.

## Approach
1. Rewrite `RedactSensitiveJsonFields` to be **text-preserving**: recursively walk the raw JSON
   text, replacing only the *values* of sensitive properties (quoted `"[REDACTED]"`) while
   copying all whitespace, key order, and non-sensitive content byte-for-byte. A clean body is
   returned as the exact same string. Invalid JSON is returned unchanged.
2. `RedactRequestBodyForLog` then naturally yields the verbatim client body (whole-body marker →
   text-preserving field redaction → raw body). No code change needed beyond the helper.
3. Update `RequestLog.RequestBody` doc comment to promise the exact client body.
4. Add regression tests.
5. Build + full test suite.
6. Commit.

## Key Files
- `Kaeo LLM Proxy Services/OllamaProxyHandler.cs` — `RedactSensitiveJsonFields`, `AppendValue`,
  `FindStringEnd`, `FindValueEnd`, `SkipValueStart`, `IsSensitiveJsonProperty`
- `Kaeo LLM Proxy Core/Models/RequestLog.cs` — `RequestBody` doc comment
- `Kaeo LLM Proxy.Tests/RequestBodyRedactionTests.cs` — 5 new tests

## Steps
- [x] 1. Rewrite `RedactSensitiveJsonFields` to be text-preserving (recursive, in-place value replacement)
- [x] 2. `RedactRequestBodyForLog` now yields the exact client body (helper no longer re-serializes)
- [x] 3. Update `RequestLog.RequestBody` doc comment
- [x] 4. Add regression tests (client `reasoning_effort` survives verbatim; sensitive fields redacted; formatting preserved)
- [x] 5. Build solution and run the test suite (68/68 passing)
- [x] 6. Git commit

## Test Results
```
Ran 68 test(s). 68 Passed, 0 Failed
```
