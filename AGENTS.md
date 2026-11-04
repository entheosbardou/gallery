# Codex instructions for Gallery

## Scope and ownership

- This repository contains the Wallpaper Gallery source copied from the live IIS app on `bens-pc.thelinclan.com`. The Gallery is the main deduplicator, organizer, and file manager. Booru and Wallpaper Desk remain independent manual downloaders with their own databases on `mysql.thelinclan.com`.
- Keep the existing downloader controls and frontend behavior unless the user explicitly requests a UI change. A backend migration alone is not authorization to redesign the interface.
- A collection is any folder containing a `_download-info` folder. Preserve this discovery rule and support inventory-only and excluded entries imported from `inventory.json` in those folders.
- Each image inventory entry has at most one `file_id`; parent or album entries have a null `file_id`. Parent/child and duplicate relationships belong in `inventory_entry_relations`.
- Keep file location authoritative in `wallpapers.files`. Do not add duplicate path or collection strings to link tables, and do not retain completed previous-location history.

## Cross-database changes

- When the Gallery moves a Booru file, update its current relative path in `booru.file_locations`. When it moves a Wallpaper Desk file, update `wallpaper-desk.files.current_path`. Preserve each downloader's post/media/file links.
- Match Gallery-discovered images to downloader posts in the downloader databases. Verify actual bytes against recorded hashes before adding `media_files` links. A path or filename match alone is insufficient.
- Keep Gallery tag IDs local and map downloader tag IDs through the external tag mapping tables. Use bounded, indexed batches for work that may grow to millions of entries.
- Before changing any database schema, report the exact proposed table, column, index, and migration changes to the user and wait for approval. Do not run migrations automatically as part of an app deployment.
- Never resolve an uncertain file operation by deleting or overwriting a file. Show the operation and its evidence; allow a resolution only after verifying the relevant file bytes and pointers.

## Build and deployment

- Build from this repository. `deploy/Build-Release.ps1` creates a release; `.github/workflows/gallery.yml` builds it on GitHub. `deploy/Deploy-OnHost.ps1` updates the existing IIS installation from a release artifact.
- Treat `C:\downloads\gallery` on `bens-pc` as the live installation. Preserve its `iis-site\web.config`, `iis-data`, NAS files, and installed Python packages during deployment. Back up replaced files and verify the immediate deployment result.
- A Dockerfile is provided for a Windows IIS image, but its image build and container runtime have not been verified. Do not claim container deployment works until it has been built and tested with the required storage and configuration mounts.
- Keep credentials, OIDC configuration, database connection files, certificates/private keys, logs, thumbnails, and downloaded images out of Git and Docker build context.
- Do not deploy to the live app, push to GitHub, or enable automatic production deployment merely because source files were edited. Follow the user's authorization for those actions.

## Communication and verification

- The user has explicitly said not to monitor anything. Do not create recurring checks, alerts, or background status reporting. A one-time check needed to verify a requested change is okay.
- Distinguish source changes, local build results, deployed changes, and completed background work. A queued job or deployed worker is not proof that recovery or matching has finished.
- Keep progress reports and final answers concise. State what changed, what was verified, and what remains unverified.
