# ResoLoop release guide

ResoLoop is distributed as a framework-dependent .NET global tool. Users need the .NET 10 SDK because installation and optional Flux-SDK workflows use `dotnet tool` commands.

## Local validation before publication

Version `0.1.0` removes the preview designation. It is currently under local validation and has not been published to GitHub Releases or NuGet. Test it in actual worlds before deciding to publish.

This version adds read-only existing-content import, external and nested member references, references across split bundle files, and explicit Slot attribute edits. See [existing import](EXISTING-IMPORT.md), [reference management](EXISTING-REFERENCES.md), and [split bundles and Slot attributes](EXISTING-BUNDLES-SLOTS.md) for behavior, limits, and validation results.

Build and test from the repository root, commit the validated source, then pack and update the installed tool from the local feed:

~~~powershell
dotnet build ResoLoop.slnx -c Release
dotnet test ResoLoop.slnx -c Release --no-build
# Commit the validated source before packaging so package metadata identifies it.
dotnet build ResoLoop.slnx -c Release --no-restore
dotnet pack src/RLoop.Cli/RLoop.Cli.csproj -c Release --no-build -o artifacts/local-feed
dotnet tool update --global ResoLoop --source ./artifacts/local-feed --version 0.1.0
resoloop --version
~~~

`--source` replaces the configured NuGet feeds for this update, so the local package is used. These commands do not publish a package, push Git commits, or create a GitHub Release. Existing projects can inspect bundled skill updates with `resoloop skills sync PROJECT --check` before choosing to sync them.

## Automated checks

`.github/workflows/ci.yml` runs on Windows for every push to `main` and every pull request. It restores, builds, runs offline tests, packs the tool, installs the resulting package into an isolated tool path, initializes a temporary project, checks bundled skills, and validates the generated apply document.

## nuget.org trusted publishing

The release workflow uses GitHub OIDC and a short-lived NuGet API key. It does not use a stored long-lived API key.

The GitHub repository may remain private. Because the distributed tool is AGPL-3.0-or-later, every NuGet package includes the matching project source and build files under `source/`. The release workflow verifies those entries before publication, so package recipients can inspect, modify, and rebuild the released version without access to the private repository.

Create a nuget.org trusted-publishing policy with these exact values:

- Owner: the nuget.org user or organization that will own `ResoLoop`
- Repository owner: `orange3134`
- Repository: `resoloop`
- Workflow file: `release.yml`
- Environment: `release`
- Package scope: exact `ResoLoop` for an existing package

For the first publication, nuget.org cannot resolve an exact package ID that does not exist yet. Create the policy with the narrow temporary glob `ResoLoop*`, publish the first version during the private-repository activation window, then immediately edit the now-permanent policy to exact `ResoLoop`. Keep unlist/relist disabled.

In the GitHub repository, create an Actions variable named `NUGET_USER` containing the nuget.org profile name, not an email address. For a private repository, complete the first publish during the temporary activation window shown by nuget.org.

## Publish an immutable release

Only after local world validation and an explicit decision to publish, push the release commit and confirm CI succeeds on that exact commit. Then dispatch `.github/workflows/release.yml` with a new stable SemVer version such as `0.1.0`. The workflow rebuilds and tests the commit, performs an installed-tool smoke test, verifies the bundled corresponding source, publishes the immutable package to nuget.org, and creates a regular GitHub Release.

~~~powershell
gh workflow run release.yml --repo orange3134/resoloop -f version=0.1.0
~~~

Never reuse a version that reached nuget.org. Use a new version for changed package contents. If package publication succeeded but GitHub Release creation failed, repair the GitHub Release separately without republishing the package.

## Consumer verification

After publication:

~~~powershell
dotnet tool install --global ResoLoop --version 0.1.0
resoloop --version
resoloop init MyResoniteProject
~~~
