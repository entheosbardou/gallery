## 2026-11-06 18:32 MST — live conveyor check

- Read-only `bens-pc` check: IIS site/pool were `Started`, the worker task was `Running`, and the worker reported at 18:31 MST. Completed image-match jobs rose from 33,539 at 17:22 MST to 38,282, about 68 per minute over the interval. Pending plus leased image-match jobs totaled 11,684. The entry and file index queues were both zero.
- The current stage report showed about 212 visual comparisons and 208 file-change completions per minute; those are stage rates, not the end-to-end rate above. The adaptive visual limit was 12; four file-change workers remained configured.
- Deduplication deadlock operations remained at zero, and no file-operation errors had been created in the preceding 15 minutes. The 49 other file-operation errors and six truncated-image hash failures remained; no image-match jobs were failed. About 800 pending image-match jobs still carried older lock-conflict retry text. No production data was changed during this check.

## 2026-11-06 17:22 MST — post-recovery live check

- Read-only `bens-pc` check: IIS site/pool were `Started`, the worker task was `Running`, and the worker reported at 17:22 MST. Completed image-match jobs rose from 31,760 at 16:08 MST to 33,539. Pending plus leased image-match jobs totaled 18,541. The entry and file index queues were both zero at the snapshot.
- The 796 deduplication file operations marked with deadlock errors at deployment were no longer in `error`; current deadlock-operation count was zero. No file-operation error had been created in the preceding 15 minutes. The status report showed about 140 visual comparisons, 119 file-change completions, and 36 index updates per minute; these are stage rates, not a measured full-process rate.
- 49 file operations still require attention for other reasons, including 12 downloader MD5 mismatches and missing paths. Six hash jobs remain failed on truncated images; no image-match jobs are failed. About 800 pending image-match jobs still carry an older temporary-lock-conflict message, with no such message dated after the 16:08 MST deployment; they remain eligible for retry. No production data was changed during this check.

## 2026-11-06 16:08 MST — contention control deployed; recovery running

- Reduced file-change concurrency from eight to four, per-worker claims from 20 to 10, and pipeline capacity from 320 to 80. The image comparison pool remains at ten initially. Structural index and duplicate-link database transactions now share a process lock; file verification and visual comparison remain outside it. No schema change.
- Python syntax, an isolated release build, and synthetic conveyor/transaction-lock checks passed. On-host release validation passed. Deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261106-160734`. The live worker files matched the staged hashes; IIS site/pool were `Started`, and the worker task was `Running`.
- The worker started its existing byte-verified recovery pass at 16:07 MST. Its initial report counted 851 open file operations; at 16:08 MST, 796 were deduplication operations marked with deadlock errors, recovery had reported 0 of 851 processed, completed image-match jobs were 31,760, and failed image-match jobs were 0. Recovery completion, the post-change deadlock rate, and sustained throughput remain unverified. No separate production repair was run.

## 2026-11-06 15:58 MST — one-hour live conveyor check

- Read-only check on `bens-pc`: IIS site and pool were `Started`; the Gallery worker task and Python process were running. The worker reported at 15:58 MST.
- Completed image-match jobs reached 31,729, up from 27,179 at the 14:52 MST deployment check. Pending/leased image-match jobs totaled about 20,700. Entry/file indexing queues were 16/23 at 15:57 MST. The six failed hash jobs were the previously known truncated images; no image-match job was in `failed` state.
- This is not a low-error run. At 15:58 MST, 673 deduplication file operations were in `error` with database deadlocks, up from 667 about a minute earlier. Another 677 pending image-match jobs carried the temporary lock-conflict retry message. The latest worker rate was only 7.5 file-change completions/minute; completed image-match count was unchanged during that one-minute check. The deadlock operations require investigation and recovery; no repair or production mutation was performed in this check.

## 2026-11-06 14:53 MST — parallel file changes and indexing deployed

- The live Gallery on `bens-pc` now allows 8 concurrent file-change workers. Four dedicated indexing threads (one entry, three file) process distinct changes while image matching continues. Tag synchronization runs in a separate thread. The pipeline remains bounded with one job claimer. Routine collection scans run every 30 minutes while image work is backlogged; explicit scan requests still run promptly.
- Indexing claims one row per transaction with `FOR UPDATE SKIP LOCKED` and generation-checked removal. Narrow file/path locks allow unrelated files in one collection to proceed together. Transient database lock conflicts requeue file jobs without using their failure allowance. Latest deployment backup: `C:\downloads\gallery\change-backups\github-release-20261106-145109`.
- Python and JavaScript syntax checks, an isolated release build, and synthetic tests for conveyor concurrency, index claims, and transient lock retries passed. On-host release validation passed. The live IIS app pool and worker task were running and deployed file hashes matched the release.
- In a brief live pipeline sample before the final lock-retry revision, the status reported about 168 visual comparisons, 125 file-change completions, and 129 index updates per minute; the index queue held 4 changes. These are stage counts, not completed image jobs. At 14:53 MST, the final revision was still recovering 134 interrupted file operations (70 processed), with 0 failed image-match jobs and 2 index changes waiting. Sustained throughput after this recovery pass remains unverified.

## 2026-11-06 14:13 MST — Initial media wait resolved

- The user confirmed that the wait before media appears is resolved after the 14:09 MST deferred-count deployment. No new browser timing value was provided. The exact Saved count and metadata queries still run after entries are displayed; their database cost has not been reduced.
- At this checkpoint, the work was on `fix/gallery-session-renewal`; the `main` worktree was not modified or deployed, and this branch had not been pushed.

## 2026-11-06 13:40 MST — Direct page jump deployed

- Both page controls now accept a page number and Enter. The API accepts `page` and uses a single offset query for that page under the existing collection/category access filters; cursor clients remain supported. Invalid or out-of-range page numbers are rejected.
- Commit `3beb277` on `fix/gallery-session-renewal` passed JavaScript syntax, diff, and release build checks. A narrow release passed preflight and deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261106-133947`. Live HTML, JavaScript, and DLL hashes matched the build. The app pool was Started and worker task Running immediately afterward.
- Authenticated page 81 interaction and production query latency remain unverified. At this checkpoint, the branch was local and unpushed.

## 2026-11-06 13:29 MST — Page controls deployed on session-renewal branch

- Added matching Previous, page number, and Next controls before and after the media cards on `fix/gallery-session-renewal`. Both controls share the existing cursor navigation state. No API or schema change.
- JavaScript syntax, diff checks, and release build passed in the isolated branch worktree. A narrow release passed preflight and deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261106-132856`. The live HTML and JavaScript SHA256 hashes matched the branch files. The app pool was Started and worker task Running immediately afterward. Authenticated browser interaction remains unverified. At this checkpoint, the branch had not been pushed.

## 2026-11-05 12:09 -07:00 — Full recovery decision controls deployed

- Duplicate issues expose keep-first, keep-second, keep-both and retry; moves expose finish-move and keep-original-location. Every issue also offers restoring a selected copy from its operation backup or locating a matching copy inside wallpaper storage. Technical details are collapsed. Queued decisions execute under the existing worker account/lock and show results beside the issue. Completed issues clear from the open panel automatically.
- Added RecoveryActions.py, using gallery_state for queued decisions/results; no schema changes. Decisions verify their plan snapshot, actual file hashes and current pointers. Merges use the existing backup and reference-update flow. Restore verifies bytes and uses exclusive creation. Completed decision payloads discard old paths. Permission, missing-byte or downloader-identity problems block unsafe choices with a message.
- Read-only inspection showed operation 14884 already complete; the permission error shown in the screenshot was stale. No ACLs or credentials were changed.
- Built in isolated .docker-stage/decisions-build. Synthetic worker checks passed for both keep directions, keep-both, retry, changed-byte rejection, restoring a located copy, overwrite prevention and wrong-copy rejection. UI checks passed for all choices, moves, queue/failure states and automatic clearing. Python/JavaScript syntax and IIS compilation passed.
- Validated and deployed to bens-pc. Backup: C:\downloads\gallery\change-backups\github-release-20261105-120857. Live UI and worker module hashes matched the release; app pool Started and worker task Running. No production merge/restore was exercised as a test; administrator decisions remain unverified live.

## 2026-11-05 11:55 -07:00 — Visible recovery actions and missing-path resolution fixed

- Fixed the missing `target_canonical_file_id` in the recovery response. The review panel now shows resolution choices directly, explains their effect, previews the saved image, and places IDs/hashes and pause controls under collapsed technical details. The pictured shared-survivor case exposes “Use saved copy and resolve”. “Keep both files and resolve” also rejects the duplicate match so this choice persists.
- Fixed resolution path checks that previously called `File.GetAttributes` on absent original paths. Expected missing leaves now pass, while unavailable storage, traversal and linked ancestors are rejected. Files and links are verified before closing an issue.
- Built in isolated `.docker-stage/review-action-build` with `Build-Release.ps1`. JavaScript synthetic interaction checks covered the screenshot case, action payload, failed verification, successful removal and keep-both visibility. Compiled C# path checks covered missing leaves, missing storage, traversal and junction rejection.
- Validated and deployed to bens-pc. Backup: `C:\downloads\gallery\change-backups\github-release-20261105-115413`. Live JavaScript hash matched the release; IIS and the worker task were running. The exact deployed recovery SELECT returned both canonical IDs and the preview collection for operation 12719. No production operation was resolved as a test; authenticated mutation remains unverified.

## 2026-11-05 11:45 -07:00 — Recovery review resolution deployed

- Added an administrator action to close a superseded deduplication operation only after the live app verifies that both old file IDs point to one present survivor, the survivor has the expected SHA-256, the old paths are absent, and no inventory or downloader bindings reference the old IDs. Failed verification leaves the operation open with a specific reason. Added lookup by operation ID in the review dialog.
- Built locally with IIS compilation plus Python and JavaScript syntax checks. Validated and deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261105-114440`. Live JavaScript hash matched the release; IIS app pool and worker task were running immediately after deployment.
- An authenticated use of the new resolution action remains unverified. Operations with no verified surviving bytes or with stale links remain open for file restoration or manual repair; the action does not delete or relink files.

## 2026-11-05 11:35 -07:00 — Recovery worker failure isolation deployed

- Live log showed recovery operation 12719 repeatedly aborting the worker startup pass with `Both paths absent`. Source now records an individual recovery operation error and continues to the next operation. Recovery operations can be held and resumed from the administrator review dialog without changing file bytes or links.
- Built the release locally (Python syntax, JavaScript syntax, and IIS compilation passed) and deployed to `bens-pc` after `Deploy-OnHost.ps1 -ValidateOnly` passed. Backup: `C:\downloads\gallery\change-backups\github-release-20261105-113422`. The live worker source hash matched the release and the scheduled worker was Running immediately after deployment.
- The log tail still contained the earlier startup failures. Completion of the next recovery pass and resolution of operation 12719 remain unverified. The missing files need separate evidence-based review; this release does not delete or relink them.

## 2026-11-05 10:51 -07:00 â€” External collection categories deployed

- Built commit `cbe648b` in an isolated worktree and deployed the release to `bens-pc` after `Deploy-OnHost.ps1 -ValidateOnly` passed. Backup: `C:\downloads\gallery\change-backups\github-release-20261105-104804`. The live `WallpaperGallery.dll` SHA256 matches the release (`BB2FEE1055C529811822E882B51D45A73C321090BD93CE9D11DC061ECA997B6F`). Immediately afterward, the Gallery app pool was Started, the database worker task was Running, and an unauthenticated collection request returned 401.
- The collection endpoint now returns accessible categories beside collection keys, without media items. The media endpoint accepts an optional exact category filter. An authenticated check through Open Wallpaper could not reach Gallery because its stored OIDC credential failed the identity match check; category payload and UI behavior remain unverified live. No account link or credential was changed.

## 2026-11-05 09:19 -07:00 â€” External collection API deployed

- Built the collection API source and deployed its release to the live Gallery IIS installation on bens-pc. Preflight passed. Deployment backup: `C:\downloads\gallery\change-backups\github-release-20261105-091731`.
- The live DLL SHA256 matches the built release (`9AAB04EFEA23E64CEED8E3A080458DE935EA5AAA7B8788B7D5ABE537D6652639`). The IIS site and app pool were Started and the worker task Running immediately afterward. Anonymous requests to `/api/external/collections` and `/api/external/media` both returned HTTP 401. The user confirmed authenticated collection listing and filtering work as expected.

# Project status

## 2026-11-06 13:15 MST — Active browser sessions renew on bens-pc

- Gallery's browser cookie previously expired 15 minutes after sign-in even while the page was active. Authenticated requests now renew it during use, up to a fixed 24-hour sign-in limit. New sign-ins receive the new cookie format; existing sessions may need one final sign-in.
- The release build passed. A narrow DLL-only release passed deployment preflight and was deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261106-131540`. The live DLL hash matched the build (`2D1C34D09FD22A19835B0984DB13D6D3ADAE2D474C93949B8D1F0D91781E549C`), the app pool was Started, and the database worker task was Running immediately afterward.
- Authenticated browser behavior across an actual renewal interval remains unverified. The change is local and unpushed. Other in-progress source changes were excluded from this deployment.

## 2026-11-06 12:25 MST — Streaming matching conveyor deployed

- Replaced the prior visual-then-mutation batch barrier with a bounded producer-consumer pipeline. One coordinator claims up to 80 match jobs at a time, with at most 160 jobs in the in-memory conveyor. Visual comparison results feed four file-change workers as they complete; the coordinator can claim the next jobs while both stages remain active. Nonmatching jobs, collection moves, recovery, and index draining remain outside this pipeline. Visual scans use a consistent database read snapshot; file-change workers recheck source/candidate state and acquire ordered locks on overlapping files and collections. Durable visual progress advances only after staged matches are applied.
- Visual capacity starts at 10 and may rise by two every 60 seconds, up to 16, when completed-job timings show the four file-change workers can consume faster than visual workers produce and the mutation queue is short. Worker status records active and waiting counts for both stages and the current visual limit. No schema changes.
- Synthetic checks passed for stage overlap, one claiming thread, bounded concurrency, adaptive scaling conditions, overlapping/disjoint file locks, deferred progress, and shared exact candidates. Python and JavaScript syntax and an isolated release build passed.
- Narrow release passed preflight and deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261106-122321`. The live worker and status files matched the release, app pool was Started, and worker task Running. The bounded follow-up still showed startup file recovery rather than matching; 32,686 match jobs were pending and six hash jobs failed. Live stage overlap, adaptive scaling, and throughput improvement remain unverified. No ongoing monitoring was started. The baseline checkpoint remains the local Git tag `gallery-before-parallel-mutation-workers-20261106` and host backup `worker-pool-baseline-20261106-115635`. Source changes are local and unpushed.

## 2026-11-06 12:09 MST — Two-stage matching worker deployed after checkpoint

- Before changes, tagged clean source commit `68faa917536cd5230490014b1301f55fb9bb8487` as local-only `gallery-before-parallel-mutation-workers-20261106`. Backed up the 500 deployed files under `C:\downloads\gallery\change-backups\worker-pool-baseline-20261106-115635` and verified every copied file hash. This checkpoint covers application files, not live MySQL or NAS data.
- The coordinator alone claims up to 80 jobs (20 per mutation worker). Up to 10 independent database connections scan visual candidates and stage matches in memory. After the scan stage, up to four workers apply exact duplicate consolidation, visual match records, and file organization. Mutation workers acquire ordered striped locks covering their source file, exact candidate files, and the involved collections. Progress cursors advance only after staged visual matches are applied. Collection moves and recovery remain outside the pool and run under the coordinator's existing global lock. No schema changes.
- Synthetic checks reached 10 visual comparisons and four disjoint mutation workers, serialized overlapping mutation sets, verified exact-candidate overlap locks, and confirmed that visual scans did not advance persistent progress before applying matches. Python syntax and an isolated release build passed.
- Deployed a narrow release to `bens-pc` after preflight; backup: `C:\downloads\gallery\change-backups\github-release-20261106-120718`. The unchanged live DLL and DatabaseWorker.py were preserved. Immediate checks showed the app pool Started, the worker task Running, and the live worker/status files matching the release. The worker's startup recovery pass completed during the bounded follow-up; the matching queue still had 32,924 pending jobs and 6 failed hash jobs, with no leased jobs at that instant. Actual 10/4 production concurrency and throughput improvement remain unverified. No continued monitoring was started. Source changes are local and unpushed.

## 2026-11-06 11:41 MST — Status page sign-in return fixed

- Live IIS requests showed the status API returning 200 until 11:36:09 MST, then 401 at 11:36:12. Gallery's preceding OIDC callback was at 11:21:12, matching the application's 15-minute browser cookie. Authentik signed in again, but Gallery's callback sent the browser to `/`. This did not represent an Authentik logout.
- The status page now sends a single reauthentication redirect when multiple component requests receive 401 together. Login stores only the allowlisted `/status.html` return path in the protected flow cookie, and callback returns there; direct unauthenticated status-page requests retain the same return path. No session lifetime or access policy changed.
- JavaScript syntax and release build passed. Narrow release passed preflight and deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261106-114047`. Immediately afterward, the app pool was Started, worker task Running, and an unauthenticated `/status.html` request redirected to `/auth/login?next=%2Fstatus.html`. The status API returned 401 without authentication. A full authenticated expiration-and-return cycle remains unverified.

## 2026-11-06 11:29 MST — Four parallel visual match comparisons deployed

- The file manager still claims up to 10 jobs per pass, but now prepares match jobs and performs exact duplicate/file mutations serially, runs up to four visual candidate comparisons on independent database connections, then finishes file organization serially. Collection moves, recovery, hash jobs, and downloader synchronization remain serialized by the coordinator. No schema changes.
- `bens-pc` had 16 logical processors and about 34 GB free physical memory during inspection. A synthetic eight-job check reached four concurrent visual comparisons and verified that file organization waited until all comparisons finished. Python syntax and release build passed.
- A narrow release passed preflight and was deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261106-112817`. The live worker source hash matched the release, the app pool was Started, and the worker task restarted as Running. The queue had 33,444 pending match jobs and six failed hash jobs before deployment. An immediate bounded follow-up still showed 33,444 pending match jobs while the worker log reported recovery operations with absent paths. Production throughput improvement and completion of the recovery pass were not verified; no continuing monitoring was started. Source changes remain local and unpushed.

## 2026-11-06 11:19 MST — Collection move status and clear job labels deployed

- Added an administrator-only Collection moves card with separate queued, running, and failed counts; individual entry/destination and file progress; failed-move retry; and recent completions in a section collapsed by default. The file-job card now identifies hashing, matching, and reconciliation jobs separately. Worker text names its recorded counts instead of saying only “Processing changes.” Recent worker, scan, download-error, and issue details are collapsed by default.
- A one-time read-only live query found no open collection moves, recent completed move 18, 33,472 pending image-matching jobs, 2 leased image-matching jobs, and 6 failed hash jobs. This is a snapshot, not monitoring.
- JavaScript syntax and release build passed. Narrow release passed preflight and deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261106-111818`. Immediately afterward, the app pool was Started, database worker task Running, anonymous status page access redirected (302), and anonymous move-status API access returned 401. Authenticated rendering and failed-move retry remain unverified live. Source changes are local and unpushed.

## 2026-11-06 11:00 MST — Maintenance and error resolution moved to admin status page

- Moved the main page's totals, worker/recovery progress, scan issues, recovery review dialog, downloader access indicators, and maintenance controls to `/status.html`. The main browsing page retains only the basic Gallery download queue indicator; detailed download errors appear on the status page. Recovery choices continue through the existing verified worker action endpoints.
- Added independently refreshing inventory and downloader-access cards. `/status.html`, `/status.js`, and `/recovery.js` are administrator-only static paths, and `/api/system-status` checks administrator role. The page's maintenance controls use the existing admin-only endpoints.
- JavaScript syntax, diff checks, and release build passed. A narrow release passed preflight and was deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261106-105934`. Immediately afterward, the app pool was Started and worker task Running. Anonymous requests to the three status page assets redirected to sign-in (302), and the status API returned 401.
- Authenticated rendering and an actual recovery decision on the page were not exercised. Source changes remain local and unpushed.

## 2026-11-06 10:50 MST — Independent system status page deployed

- Added an administrator-only `/status.html` page linked from Gallery. Worker, file work queue, recovery, latest scan, download queue, and issue examples each request their own read-only `/api/system-status?component=...` data and refresh independently about every 15–30 seconds. Each card shows its own check time or error; the worker card also flags stale updates.
- Local release build, JavaScript syntax, and diff checks passed. A narrow release was validated and deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261106-104923`. Live page, script, and DLL hashes matched the release. The app pool was Started, the database worker task Running, and unauthenticated API access returned 401 immediately afterward.
- Authenticated rendering and per-component API responses were not verified live. No monitoring was started. Source changes remain local and have not been pushed.

## 2026-11-05 19:06 MST — Gallery batch lookup confirmed with tablet sync

- The user confirmed that Open Wallpaper successfully synced the Gallery playlist to the tablet after the paired deployment. This is user-reported end-to-end confirmation of the authenticated batch lookup in the working integration. Individual denied-file behavior and the exact deployed Open Wallpaper image digest were not independently checked here.

## 2026-11-05 19:01 MST — Batch media lookup deployed

- Built the Gallery release in an isolated copy and staged only the new `WallpaperGallery.dll` plus unchanged live files required by the deploy validator. `Deploy-OnHost.ps1 -ValidateOnly` passed on `bens-pc`, then the narrow release was deployed. Backup: `C:\downloads\gallery\change-backups\github-release-20261105-190019`.
- The live DLL SHA256 matches the built release (`3788EA716080A53E8A077705A182D4CDEC5838B8DB1F97F31B9BDDD8454C758A`). The site and app pool were Started and the database worker task Running immediately afterward. An unauthenticated `POST /api/external/media/resolve` returned HTTP 401. Authenticated batch decisions and Open Wallpaper tablet sync remain unverified live.

## 2026-11-05 18:51 MST — Batch media permission lookup in source

- Added `POST /api/external/media/resolve` to check up to 500 file IDs with one OIDC identity and return current paths for allowed files. The shared lookup now applies the caller's collection and category grants. This supports Open Wallpaper device sync without per-image catalog requests or a stored Gallery device grant. An isolated `Build-Release.ps1` build succeeded with compiler dependency warnings. Source only; deployment and live permission checks remain outstanding.

The original repository snapshot and later timestamped entries record evidence available at their respective observation times. This is not a live status monitor.

## 2026-11-04 23:20 MST â€” External media connection active

- Gallery source for the external API and rotated refresh-token response is in this repository, including pushed commit `905fd04`. The release built from this repository was copied from `BENS-LAPTOP` to `bens-pc`. Elevated `Deploy-OnHost.ps1 -ValidateOnly` passed there, then deployment completed with backup `C:\downloads\gallery\change-backups\github-release-20261104-224844`. The live DLL hash matched the repository release; the IIS app pool was Started and the worker task Running at the bounded check.
- The host-only live `C:\downloads\gallery\iis-data\oidc.json` on `bens-pc` was backed up as `oidc.json.pre-external-20261104-231234.bak`, then configured with external client settings matching its existing OIDC provider. This was a configuration change, not a source change; credentials remain outside the repository. The app pool was restarted to load the settings.
- The shared Authentik provider now enables the Refresh token grant and `offline_access` scope mapping; its Gallery roles mapping remains selected. A signed ID-token catalog request from the Open Wallpaper container returned HTTP 200 with an item in the requested page. The user subsequently confirmed Gallery media is visible in Open Wallpaper.
- Device-grant creation, item revocation, outage behavior, and Android playback have not been verified end to end in this status update. The additive Gallery grant tables were previously applied to the live database without a database backup at the user's direction. `current-changes.md` is reset for the next session and remains untracked.

## 2026-11-04 20:08 MDT â€” External media API source work

- Added optional bearer-token endpoints for a permission-filtered external catalog, device grant creation, per-file grant authorization, and owner/admin revocation. Grant checks refresh OIDC identity and evaluate the current Gallery group, collection, and rating/category policy per requested file.
- Added an additive SQL file defining `external_device_grants` and `external_device_grant_items`. The tables store hashed grant IDs, encrypted refresh tokens, device/owner binding, state/expiry, and authorized file IDs. Application startup does not run the migration.
- The local release build completed successfully after adding the Gallery source. The existing compiler emitted its prior assembly-version warnings.
- The live MySQL migration was attempted using the Gallery runtime database account and was rejected because that account lacks `CREATE` permission. The first table was not created; the failed DDL did not modify the database. No database backup was taken, per the user's instruction.
- The live `iis-data\oidc.json` does not yet contain the external OIDC client ID or secret, so the new API remains disabled until an external OIDC client is configured. No source deployment or production API verification has occurred at this status entry.
- `current-changes.md` is temporary local working context and must remain out of commits.

## 2026-11-04 20:15 MDT â€” Grant schema applied; deployment retry prepared

- The user granted the Gallery MySQL account the needed `CREATE` permission. Applied `external-device-grants-schema.sql` without a database backup per the user's direction. The migration verified both `external_device_grants` and `external_device_grant_items` in the live Gallery database.
- Two deployment attempts hit an IIS app-pool start race after stopping the pool. The deployment script's rollback restored the previous Gallery files. The app pool was manually returned to `Started`; the prior application remains live.
- Updated `Deploy-OnHost.ps1` to wait for the app pool to reach `Stopped` before replacing files and `Started` after restart, with a bounded recovery wait after rollback. This retry fix has not yet been built or deployed.
- The external OIDC client settings are still absent from live `iis-data\oidc.json`. The optional bridge remains disabled until that client is configured.

## 2026-11-04 21:18 MDT â€” Gallery release deployed

- Commit `56e2c92` added the optional Gallery API and additive grant schema; commit `b609a6a` added bounded IIS app-pool transition waits. Both commits were pushed to `origin/main`.
- Release build completed successfully. The updated deployment completed on `bens-pc`; the deployment script recorded its file backup under `C:\downloads\gallery\change-backups\github-release-20261104-201732`.
- Post-deployment checks confirmed the Gallery IIS app pool and site are `Started`, the database worker task is `Running`, and the live Gallery DLL matches the deployed release.
- The two grant tables exist in the live database. No database backup was taken, per the user's instruction.
- The external OIDC client ID and secret are not configured in live `oidc.json`; therefore the optional API is deployed but not enabled. User-token authorization and device-grant requests have not been exercised against a configured external OIDC client.
- Local `current-changes.md` remains untracked and excluded from both commits.

## Working or verified

- The current Gallery source was copied from `C:\downloads\gallery` on `bens-pc.thelinclan.com` into this repository. The live installation uses IIS/.NET Framework 4.8, a Python database worker, MySQL, and NAS wallpaper storage.
- A local release built successfully with `deploy/Build-Release.ps1`. C#, Python syntax, JavaScript syntax, PowerShell syntax, and `web.config` XML checks passed.
- `deploy/Deploy-OnHost.ps1 -ValidateOnly` passed against the existing live host without changing it. Its deployment path backs up replaced files and preserves live configuration/data.
- The live Gallery has a file-operation review view with paths, expected hashes, current links, image previews, retry, and a guarded **Keep both files** action. Its C# assembly compiled and was deployed in the prior work.
- Downloader post matching was added to the live Gallery worker. One known Booru image was verified against a linked post under the worker's Windows account. Booru's `post_media.remote_md5` lookup index was added.

## Known problems or unresolved work

- Old file-operation recovery had a large backlog. At the last one-time check, an operation had a downloader MD5 mismatch and required review. Recovery completion was not verified.
- Existing downloader-image matching was queued for 10,517 images. Completion and any failures were not verified. The user requested no monitoring.
- The Dockerfile has not been built or run. Docker Desktop's service was stopped, and this shell could not start it. Windows container mode, NAS mounts, IIS access, and OIDC flow still need a container test.
- The GitHub Actions workflow has not been exercised. Production deployment requires a self-hosted Windows runner labeled `gallery` on `bens-pc` and a `production` environment in GitHub. Runner setup was not verified.
- The repository's deploy script has passed preflight only; no deployment from this repository or from GitHub has been performed.

## Next verification when requested

1. Build the Windows Docker image on a Windows-container Docker engine and test it with nonproduction mounts.
2. Run the GitHub build workflow, then configure the `gallery` runner and manually test the deployment workflow.
3. Review unresolved file operations through the Gallery UI and confirm the downloader matching backlog when the user asks for a one-time status check.
## 2026-11-06 13:50 MST — Initial inventory load deployed

- On `fix/gallery-session-renewal`, Gallery now starts the initial inventory request without waiting for the download-status request. The page still requests and displays download status independently. Updated the Gallery script URL so browsers fetch the change.
- JavaScript syntax, diff check, and release build passed. A narrow release with the two changed UI files and unchanged live server companions passed deployment preflight and deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261106-134951`. Live UI hashes matched the branch files; IIS app pool was Started and database worker task Running immediately afterward. Authenticated browser timing and production inventory latency remain unverified. The branch is local and unpushed. The uncommitted changes on `main` were not touched or deployed.

## 2026-11-06 14:09 MST — Saved results display before exact count

- Live IIS logs showed `/api/inventory` taking roughly 12–15 seconds for `statuses=["saved"]` at 100 records per page. Read-only reproduction on the live database measured the Saved count at 11.2 seconds, while fetching the first 101 saved IDs took 0.04 seconds. The inventory metadata saved-file count took 7.8 seconds. These are observations, not authenticated browser timings.
- On `fix/gallery-session-renewal`, `defer_count=1` lets the inventory API return entries without the slow exact count; a new access-controlled `GET /api/inventory-count` supplies the exact total afterward. The browser now renders entries first, waits 750 ms to prioritize images, then requests the count and metadata. Page navigation and access checks remain in place. No schema change.
- JavaScript syntax, diff check, and release build passed. A narrow release containing only the compiled Gallery handler and the updated Gallery page/script passed preflight and deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261106-140918`. Live file hashes matched the build; the app pool was Started and worker task Running immediately afterward. The `main` worktree and its uncommitted files were not touched or deployed. Updated authenticated first-media timing remains unverified. The branch is local and unpushed.

## 2026-11-09 13:32 MST — Saved image category change (source only)

- Added an administrator-only Change category action for saved image entries. It queues the existing verified file-move worker to move the file into SFW, Sketchy, NSFW, or Not provided, then updates the entry category. The dialog reports move progress and errors and supports retrying a failed move.
- The action currently requires exactly one saved file used by no other active entry. Shared files and entries with multiple linked files return a review message, avoiding an ambiguous access-category change.
- No schema change or migration. An isolated release build passed, including C# compilation and Python syntax validation; JavaScript syntax and Git whitespace checks passed. This is source only: no deployment, live database mutation, or authenticated browser test was performed.

## 2026-11-09 13:45 MST — Saved image category change deployed

- Deployed the category-change source from the local worktree to the IIS installation on bens-pc using a narrowed release. The release was validated with Deploy-OnHost.ps1 -ValidateOnly before installation. The deployment script backed up replaced files to C:\downloads\gallery\change-backups\github-release-20261109-134503.
- Immediate checks found matching hashes for the new worker, page, JavaScript, compiled Gallery handler, and changed C# source. The WallpaperGallery app pool was Started and the database worker task Running. An unauthenticated request to http://bens-pc.thelinclan.com:8791/ returned the expected 302 sign-in redirect.
- No saved image was reclassified during verification. Authenticated dialog behavior, file relocation, external downloader path sync, and category access after a real move remain unverified live. The category-change source is in local commit `5f4c763`; the deployment-status note is uncommitted. No push was performed in this deployment.

## 2026-11-09 13:56 MST — Bulk category action deployed

- Added an administrator-only Change selected categories button beside bulk collection move. It queues the existing category-change operation for each eligible selected saved image, skips entries already in the target category, and reports entries that could not be queued. Non-image and unsaved selections are excluded from the eligible count.
- JavaScript syntax, diff check, and an isolated release build passed. A narrow UI release passed `Deploy-OnHost.ps1 -ValidateOnly` and deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261109-135601`.
- Immediate checks found matching hashes for the deployed page and JavaScript. The app pool was Started, the database worker task Running, and an unauthenticated request returned the expected 302 sign-in redirect. Authenticated browser behavior and an actual bulk category move remain unverified live.

## 2026-11-09 14:08 MST — Hidden duplicate links and category moves

- Live read-only inspection confirmed AlphaCoders 883689 and 883679 are visible saved primary entries linked to hidden Wallhaven duplicate entries; AlphaCoders 1377548 is linked to a hidden Local duplicate. The primary records retained `not_provided` because the earlier category requests were rejected.
- Updated paged inventory to show the count of hidden duplicate entries linked to each primary file. A category move now permits links from hidden duplicate image records and updates their category along with the visible primary record. Visible shared entries and non-image links still block the change for review.
- JavaScript and Python syntax checks, diff check, and isolated release build passed. A narrowed release passed preflight and deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261109-140757`. Immediate deployed hashes matched; the app pool was Started, worker task Running, and the site returned its normal 302 sign-in redirect.
- No image was recategorized during verification. Authenticated display and actual category-move behavior remain unverified live.

## 2026-11-09 14:21 MST — Duplicate labels inherit the strictest source label

- Implemented site-wide duplicate-group category inheritance in the live file-manager worker. The visible primary entry takes the most restrictive original label among itself and its hidden duplicates: `nsfw > sketchy > sfw > not_provided`. The original primary label is kept in the record so a later source update can recalculate the group without making an inferred label sticky. Source imports and manual category changes update that original label as appropriate.
- Updated the file access ranking to the same order. Removed the “Also linked to n hidden duplicate entries” text from Gallery. Primary cards retain the category-change action. No schema change or migration.
- A bounded worker backfill will apply the rule to existing duplicate groups. At the single immediate post-deployment check, its cursor had reached entry ID 2667; completion was not verified. AlphaCoders 883689 had not yet been reached and still displayed `not_provided` at that moment.
- Python and JavaScript syntax checks, targeted precedence tests, diff check, and an isolated release build passed. The narrowed release passed preflight and deployed to `bens-pc`; backup: `C:\downloads\gallery\change-backups\github-release-20261109-142104`. Immediate deployed hashes matched, the app pool was Started, the database worker task Running, and the site returned its normal 302 sign-in redirect. Authenticated UI and complete backfill remain unverified.
