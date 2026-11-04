# Project status

Snapshot: November 4, 2026. This records the evidence available when the repository was prepared; it is not a live status monitor.

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
