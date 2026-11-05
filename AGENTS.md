# Codex instructions for Gallery

## Temporary session instructions

- Before development work in this repository, read `current-changes.md` when it exists. Treat it as temporary working context for the active development initiative/session: record current scope, decisions, clarifications, open questions, and constraints, including work contained entirely in this repository. Update it as the user and development work clarify or change the initiative. It is not permanent policy; follow the latest user direction if there is a conflict. Do not move temporary decisions into this file unless the user asks for a standing instruction.
- Keep `current-changes.md` focused on current scope, decisions, open questions, and implementation constraints. Remove or revise stale items when the initiative changes.

## Scope and ownership

- This repository contains the Wallpaper Gallery source. The Gallery is the main deduplicator, organizer, and file manager. Booru and Wallpaper Desk remain independent manual downloaders with their own databases on `mysql.thelinclan.com`.
- Keep the existing downloader controls and frontend behavior unless the user explicitly requests a UI change. A backend migration alone is not authorization to redesign the interface.
- A collection is any folder containing a `_download-info` folder. Preserve this discovery rule and support inventory-only and excluded entries imported from `inventory.json` in those folders.
- Each image inventory entry has at most one `file_id`; parent or album entries have a null `file_id`. Parent/child and duplicate relationships belong in `inventory_entry_relations`.
- Keep file location authoritative in `wallpapers.files`. Do not add duplicate path or collection strings to link tables, and do not retain completed previous-location history.

- Verify the configured wallpaper root is accessible before reconciling file presence; an unavailable share must not be treated as missing media. Keep resolved file operations within that root and preserve path traversal and linked-path protections.

## Cross-database changes

- When the Gallery moves a Booru file, update its current relative path in `booru.file_locations`. When it moves a Wallpaper Desk file, update `wallpaper-desk.files.current_path`. Preserve each downloader's post/media/file links.
- Match Gallery-discovered images to downloader posts in the downloader databases. Verify actual bytes against recorded hashes before adding `media_files` links. A path or filename match alone is insufficient.
- Keep Gallery tag IDs local and map downloader tag IDs through the external tag mapping tables. Use bounded, indexed batches for work that may grow to millions of entries.
- Before changing any database schema, report the exact proposed table, column, index, and migration changes, affected data, backup, rollback, and verification steps to the user and wait for approval. Do not run migrations automatically as part of an app deployment.
- Never resolve an uncertain file operation by deleting or overwriting a file. Show the operation and its evidence; allow a resolution only after verifying the relevant file bytes and pointers.

## Build and deployment

- When source is updated, update the files in the folder containing this AGENTS.md file.
- Build from this repository. `deploy/Build-Release.ps1` creates a release; `.github/workflows/gallery.yml` builds it on GitHub. `deploy/Deploy-OnHost.ps1` updates the existing IIS installation from a release artifact.
- Treat `C:\downloads\gallery` on `bens-pc` as the live installation. Preserve its `iis-site\web.config`, `iis-data`, NAS files, and installed Python packages during deployment. Back up replaced files and verify the immediate deployment result.
- The Dockerfile targets Windows IIS. Verify image builds and container behavior with the required storage and configuration mounts before claiming that container deployment works.
- Keep credentials, OIDC configuration, database connection files, certificates/private keys, logs, thumbnails, and downloaded images out of Git and Docker build context. Never print secret values in commands, logs, tests, or responses. Preserve TLS verification and existing authentication boundaries.
- Do not deploy to the live app, push to GitHub, or enable automatic production deployment merely because source files were edited. Follow the user's authorization for those actions.

## Verification

- For relevant changes, build with deploy/Build-Release.ps1 in an isolated working copy, check changed Python and JavaScript syntax, parse changed PowerShell scripts and web.config, and test affected behavior with synthetic data or a separate test database. Do not exercise migrations, cleanup, or file moves against production as a test.
- Verify collection discovery, inventory-only/excluded entries, parent/child and duplicate links, downloader references, and access controls when those paths change. Preserve category and collection visibility checks on media and API access.
- Before an authorized deployment, validate the release with deploy/Deploy-OnHost.ps1 -ValidateOnly on the intended host. Afterward, perform bounded immediate checks of IIS, worker startup, and the affected feature; report limitations.

## No monitoring unless requested

- Do not monitor, poll, tail logs continuously, create recurring checks or alerts, schedule follow-ups, or produce background status reports unless the user explicitly requests monitoring. Starting a job, making a fix, or deploying a worker does not authorize monitoring it.
- A bounded, one-time check needed to verify an authorized change is allowed. It must not become a retry loop or ongoing observation. For a long-running job, report that it started and stop observing; inspect its progress or completion only when the user asks.
- This rule governs agent observation and automation. It does not authorize disabling the application's existing workers, queues, or health checks.

## Status records and communication

- Keep AGENTS.md limited to standing application instructions. Record deployment state, verification results, known problems, unfinished work, and other status items in PROJECT_STATUS.md, not here.
- Timestamp each status entry with the observation date, time, and timezone. Distinguish observed facts from assumptions, identify the relevant source revision or deployed artifact when available, and state what remains unverified. Historical entries are not proof of current state.

- Distinguish source changes, local build results, deployed changes, and completed background work. A queued job or deployed worker is not proof that recovery or matching has finished.
- Keep progress reports and final answers concise. State what changed, what was verified, and what remains unverified.
