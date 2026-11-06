# Wallpaper Gallery

This repository contains the source copied from the live Wallpaper Gallery on `bens-pc.thelinclan.com` on November 4, 2026. The app consists of an ASP.NET Framework 4.8 IIS site and a Python file/database worker. The existing IIS installation is at `C:\downloads\gallery`.

## Build and deploy from GitHub

The [Gallery workflow](.github/workflows/gallery.yml) builds on each push and pull request. It compiles the IIS DLL and helper executables, checks Python syntax, and publishes a `gallery-release` artifact. Pushing code does not automatically replace the live site.

To enable the **Deploy** option in GitHub Actions:

1. Create a private GitHub repository and push this local repository to it.
2. Install a GitHub Actions self-hosted **Windows** runner on `bens-pc`, with the label `gallery`. Its account needs access to the existing IIS site, the `Wallpaper Gallery Database Worker` scheduled task, and `C:\downloads\gallery`. Register it as a service so it can receive a manually requested deployment.
3. In GitHub, create the `production` environment and restrict who may approve deployments.
4. Open **Actions → Gallery build and deploy → Run workflow**, select **Deploy the built release to bens-pc**, and run it. The deploy job backs up files, stops the worker and IIS app pool, copies the release, and restarts them. It preserves `iis-site\web.config`, `iis-data`, and installed Python packages. A failed copy restores the backed-up files.

The workflow does not apply database migrations. Apply reviewed schema changes separately before deploying code that requires them.

To build a release locally on Windows, run `powershell -File .\deploy\Build-Release.ps1`. The result is in `.docker-stage\release`. To check a release against the live host without modifying it, run `powershell -File .\deploy\Deploy-OnHost.ps1 -ReleasePath .docker-stage\release -ValidateOnly` on `bens-pc`.

## Docker image

The [Dockerfile](Dockerfile) builds a Windows IIS image from the same source. It installs Python 3.14.8 and the pinned [worker packages](requirements-worker.txt), compiles the .NET Framework app, and runs IIS and the Python worker. Build it with a Docker engine in **Windows container** mode:

```powershell
docker build -t gallery:local .
```

For a container run, provide a writable local bind mount for the NAS wallpaper directory at `C:\wallpapers` and a separate bind mount containing `mysql.json`, `oidc.json`, and `mysql-ca.pem` at `C:\downloads\gallery\iis-data`. The container changes its `WallpaperRoot` setting to `C:\wallpapers` at startup. The image contains no database password, OIDC secret, API key, or NAS files. The existing GitHub deployment workflow uses the native IIS release artifact, keeping the current host bindings and downloader controls.

Docker Desktop on `bens-pc` was installed but its daemon was stopped when this repository was prepared, so the Windows image has not yet been built there. Windows container mode and host storage mounts need to be configured before using the image as a running service.

## Optional external media API

The Gallery can expose a read-only, OIDC-authorized media catalog and device grant API for an external media client. The API is disabled until the existing `iis-data\oidc.json` contains `externalClientId` and `externalClientSecret`. These must identify an OIDC client registered with the same issuer as `authority`; the external client must support refresh tokens and include the `gallery_groups` claim. The integration client sends its signed ID token as a bearer token and must use the same client credentials for refresh-token exchange.

Before deploying the API, apply `iis-helper\external-device-grants-schema.sql` to the Gallery database. The migration adds only `external_device_grants` and `external_device_grant_items`; it does not change existing Gallery rows. Startup does not apply this migration automatically.

- `GET /api/external/collections` returns accessible collection keys and a `categories` object mapping each key to its accessible category names, without media items. Authenticate with `Authorization: Bearer <OIDC ID token>`.
- `GET /api/external/media?collection=<key>&category=<name>&after=<file-id>&limit=<1-250>` returns a bounded page of present canonical files allowed by the caller's current Gallery group, collection, and rating/category rules. Collection and category are optional exact-match filters. Authenticate with `Authorization: Bearer <OIDC ID token>`.
- `POST /api/external/media/resolve` accepts `Authorization: Bearer <OIDC ID token>` and JSON `file_ids` (1–500 IDs). It returns one `allowed` or `denied` decision per ID. Allowed items include the current Gallery-relative path and media type for a separately mounted, read-only file share. Decisions use the caller's current collection and category permissions; no device grant is required.
- `POST /api/external/grants` creates a device grant for the subset of submitted `file_ids` currently allowed to the user. The JSON body contains `client_instance`, `device_id`, `file_ids`, and the user's OIDC `refresh_token`. The one-time `grant_token` is returned only at creation; store it securely in the external server, never on the device.
- `POST /api/external/grants/authorize` accepts `X-Gallery-Device-Grant: <grant_token>` and JSON `client_instance`, `device_id`, and requested `file_ids`. It refreshes the user's OIDC identity, checks the current Gallery policy per file, and returns `allowed`, `denied`, or `not_assigned` for each item. Allowed items include the current Gallery-relative path for a separately mounted, read-only file share.
- `POST /api/external/grants/revoke` accepts the user's bearer ID token and JSON `grant_token`. The owner or a Gallery administrator can revoke it.

The API does not modify Gallery catalog records or files. External clients remain responsible for isolating mounted Gallery files from their own media-management paths. Configure the OIDC client and keep its credentials in host-side `iis-data`; never commit them.

## Source layout

- `iis-source`: live C# source and IIS build scripts.
- `iis-helper`: live Python worker, SQL migrations, and required .NET library DLLs in `runtime-lib`.
- `iis-site`: live HTML, JavaScript, ASP.NET application file, and a copy of `web.config` for image builds.
- `deploy`: release build and deployment scripts.

Secrets and runtime data stay on the host. Do not add `iis-data`, downloaded images, logs, or private keys to Git.
