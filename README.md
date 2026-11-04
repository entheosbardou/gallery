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

## Source layout

- `iis-source`: live C# source and IIS build scripts.
- `iis-helper`: live Python worker, SQL migrations, and required .NET library DLLs in `runtime-lib`.
- `iis-site`: live HTML, JavaScript, ASP.NET application file, and a copy of `web.config` for image builds.
- `deploy`: release build and deployment scripts.

Secrets and runtime data stay on the host. Do not add `iis-data`, downloaded images, logs, or private keys to Git.
