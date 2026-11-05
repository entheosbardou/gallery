# Project status

Snapshot: November 4, 2026. This records the evidence available when the repository was prepared; it is not a live status monitor.

## 2026-11-04 20:08 MDT — External media API source work

- Added optional bearer-token endpoints for a permission-filtered external catalog, device grant creation, per-file grant authorization, and owner/admin revocation. Grant checks refresh OIDC identity and evaluate the current Gallery group, collection, and rating/category policy per requested file.
- Added an additive SQL file defining `external_device_grants` and `external_device_grant_items`. The tables store hashed grant IDs, encrypted refresh tokens, device/owner binding, state/expiry, and authorized file IDs. Application startup does not run the migration.
- The local release build completed successfully after adding the Gallery source. The existing compiler emitted its prior assembly-version warnings.
- The live MySQL migration was attempted using the Gallery runtime database account and was rejected because that account lacks `CREATE` permission. The first table was not created; the failed DDL did not modify the database. No database backup was taken, per the user's instruction.
- The live `iis-data\oidc.json` does not yet contain the external OIDC client ID or secret, so the new API remains disabled until an external OIDC client is configured. No source deployment or production API verification has occurred at this status entry.
- `current-changes.md` is temporary local working context and must remain out of commits.

## 2026-11-04 20:15 MDT — Grant schema applied; deployment retry prepared

- The user granted the Gallery MySQL account the needed `CREATE` permission. Applied `external-device-grants-schema.sql` without a database backup per the user's direction. The migration verified both `external_device_grants` and `external_device_grant_items` in the live Gallery database.
- Two deployment attempts hit an IIS app-pool start race after stopping the pool. The deployment script's rollback restored the previous Gallery files. The app pool was manually returned to `Started`; the prior application remains live.
- Updated `Deploy-OnHost.ps1` to wait for the app pool to reach `Stopped` before replacing files and `Started` after restart, with a bounded recovery wait after rollback. This retry fix has not yet been built or deployed.
- The external OIDC client settings are still absent from live `iis-data\oidc.json`. The optional bridge remains disabled until that client is configured.

## 2026-11-04 21:18 MDT — Gallery release deployed

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
