# Current changes — Gallery

## Manual recovery precedence

- A reviewed administrator decision must take precedence over automated checks in every retry, error, and failed queue. This includes file jobs, file operations, collection and category moves, scans, and downloads. Keep the decision across retries and worker restarts; invalidate it only if the relevant file bytes, pointers, or reviewed record change.
- Offer a manual path for every failure that can still be processed. Show the evidence and the effect before applying the decision. Physical impossibility, such as missing bytes without a usable copy, remains a valid blocker.
- Current implementation covers reviewed partial-image acceptance and downloader checksum correction for failed file jobs, plus existing reviewed file-operation choices. Audit and fill the remaining queue-specific gaps before describing this requirement as complete.
- Audit gaps: add reason-specific reviewed actions for other file-job failures; manual per-file completion or alternative for failed collection/category moves; durable per-entry download errors and choices; per-item scan/import issue review; visible handling for repeated index/tag-sync failures. Ensure broad worker retries never erase or bypass a pending manual decision.
