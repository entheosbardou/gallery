# Current changes — Gallery

## Manual recovery precedence

- A reviewed administrator decision must take precedence over automated checks in every retry, error, and failed queue. This includes file jobs, file operations, collection and category moves, scans, and downloads. Keep the decision across retries and worker restarts; invalidate it only if the relevant file bytes, pointers, or reviewed record change.
- Offer a manual path for every failure that can still be processed. Show the evidence and the effect before applying the decision. Physical impossibility, such as missing bytes without a usable copy, remains a valid blocker.
- Current implementation covers reviewed partial-image acceptance and downloader checksum correction for failed file jobs, plus existing reviewed file-operation choices. Audit and fill the remaining queue-specific gaps before describing this requirement as complete.
- Audit gaps: add reason-specific reviewed actions for other file-job failures; manual per-file completion or alternative for failed collection/category moves; durable per-entry download errors and choices; per-item scan/import issue review; visible handling for repeated index/tag-sync failures. Ensure broad worker retries never erase or bypass a pending manual decision.

## Duplicate storage rule

- For confirmed duplicate media, retain one physical file and relink all posts, entries, downloader locations, and media associations to it. Remove each redundant physical file after verifying both copies and the links. Do not retain separate backup copies of duplicates. A rejected match is not a confirmed duplicate and may keep separate files.
- Existing failed cleanup operations for the same pair must be resumed rather than multiplied. Remove stale duplicate backup copies only after the surviving file and every reference have been verified and all related operations are terminal.
- Automatically close superseded duplicate operations when both retired IDs already point to one present survivor, neither old path or link remains, and the survivor bytes match the recorded SHA-256. Keep the manual button for exceptional cases.
- The one-time legacy duplicate-backup cleanup started on 2026-11-09 and may still be running. Its status is recorded in `PROJECT_STATUS.md`; do not infer completion from deployment. Once all posts and locations have been relinked to the canonical file, delete the obsolete `booru.files` row. Preserve the post and media records and their links to the surviving file.
