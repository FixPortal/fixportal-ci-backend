# Lizard scanner failure-boundary proof — 16 October 2026

The prior ScanAsync path test returned before the try/finally and could not qualify ordinary failure containment or cleanup. New tests drive the real scanner to token acquisition, verify its initial clone cleanup, recreate only a test-owned clone at that boundary, then inject ordinary failure, timeout-shaped cancellation without caller shutdown, or actual caller shutdown. Assertions require null for contained failures, propagated caller cancellation, and finally removal of the recreated clone.

Independent production mutations: omitted finally cleanup3assertion failures; omitted cancellation filter1failure; swallowed caller shutdown1failure; escaping ordinary failure1failure. Restored production SHA matched; selected6passed. Compile-only rejected mutations are excluded from evidence.

Full isolated backend gate:640passed0failed0skipped; build0warnings/errors; formatting97files; CI-equivalent dotnet-coverage/TRX wrapper generated retained artifacts. Disposable SDK container had no host mounts/socket/secrets and no external network during tests; stopped and retained. Ownership witnesses and resolved paths bound/identify fixture cleanup; no host tests ran.

This proves the common D15/E13 failure/null/finally/cancellation paths. Injected timeout-shaped cancellation is not evidence of actual ProcessRunner timeout. Actual git failure/redaction, lizard process/output behavior and G17 image qualification remain separate queued scenarios. Retained detailed report: ci-backend-lizard-proof/REPORT.md in the session evidence directory.
