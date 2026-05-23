Create a ticket for adding structured logging to the broker integration layer.

Type: enhancement
Priority: medium

Goal: replace ad-hoc logging (Console.WriteLine, Debug.WriteLine, raw Trace calls) in the broker integration code with structured logging via Microsoft.Extensions.Logging. Every outbound HTTP call to a broker API should log: the endpoint, the HTTP method, the request ID (if assigned), the response status, and the round-trip duration. Failures log at warning or error level with the exception. Successful calls log at debug level.

Acceptance criteria:
- All broker integration classes use ILogger<T> injected via constructor, not static loggers
- Outbound API calls produce a single structured log entry per request with the listed fields
- No Console.WriteLine or Debug.WriteLine remain in broker integration files
- Existing tests still pass; tests relying on console output get updated
- A short note in docs/logging.md explains the convention so future code follows it