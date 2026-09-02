# ResoLoop release guide

ResoLoop is distributed as a framework-dependent .NET global tool. Users need the .NET 10 SDK because installation and optional Flux-SDK workflows use `dotnet tool` commands.

## Automated checks

`.github/workflows/ci.yml` runs on Windows for every push to `main` and every pull request. It restores, builds, runs offline tests, packs the tool, installs the resulting package into an isolated tool path, initializes a temporary project, checks bundled skills, and validates the generated apply document.

## nuget.org trusted publishing

The preview release workflow uses GitHub OIDC and a short-lived NuGet API key. It does not use a stored long-lived API key.

The GitHub repository may remain private while preparing the preview. Because the distributed tool is AGPL-3.0-or-later, the workflow refuses to publish the binary package to public nuget.org until the corresponding-source repository is public.

Create a nuget.org trusted-publishing policy with these exact values:

- Owner: the nuget.org user or organization that will own `ResoLoop`
- Repository owner: `orange3134`
- Repository: `resoloop`
- Workflow file: `release.yml`
- Environment: `release`
- Package scope: `ResoLoop`

In the GitHub repository, create an Actions variable named `NUGET_USER` containing the nuget.org profile name, not an email address. For a private repository, complete the first publish during the temporary activation window shown by nuget.org.

## Publish an immutable preview

Confirm CI succeeds on the exact commit and make the repository public, then dispatch `.github/workflows/release.yml` with a new SemVer prerelease version such as `0.1.0-preview.1`. The workflow rebuilds and tests the commit, performs an installed-tool smoke test, verifies public corresponding-source access, publishes the immutable package to nuget.org, and creates a GitHub prerelease.

~~~powershell
gh workflow run release.yml --repo orange3134/resoloop -f version=0.1.0-preview.1
~~~

Never reuse a version that reached nuget.org. Increment the prerelease number even when a failed GitHub Release must be retried after the package publish succeeded.

## Consumer verification

~~~powershell
dotnet tool install --global ResoLoop --version 0.1.0-preview.1
resoloop --version
resoloop init MyResoniteProject
~~~
