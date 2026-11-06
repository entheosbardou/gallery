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
