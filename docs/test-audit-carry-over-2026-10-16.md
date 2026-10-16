# Test audit A26: cached review-signal carry-over

A two-case test starts the actual enrichment worker and drives two sweeps with a fake cadence. An unchanged PR is carried while a second PR is fetched. It verifies the real second query excludes the carried PR, the cached row uses the current reviewer roster, and repository-scoped secret counts reach both carried and fetched rows. The control retains the unchanged roster.

Two independent compiling guest mutations bypass roster filtering and carried-row secret refresh. They respectively fail one and two intended assertions. Production is restored exactly. All115 ReviewSignal tests pass, zero skips. Full solution Rebuild succeeds with zero errors and one existing unrelated S3776 warning; formatting and diff checks pass.

The selected worker/test paths have no file cleanup or process children. Execution used an owned SDK guest with scripted HTTP, no host mounts/socket/ports/credentials; it is stopped. Full hosted CI remains required. A26 eviction remains a separate open child; this is not blanket A26 closure.
