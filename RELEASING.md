# Releasing

The `release` workflow (`.github/workflows/release.yml`) builds versioned
**managed** and **unmanaged** solution zips from the templates in `Solutions/`,
swapping in the freshly built `Celedon.AutoNumber` assembly and stamping the
solution version. Packaging is done by `scripts/Build-SolutionArtifacts.ps1`.

## Versioning scheme

Solution versions are four-part: `Major.Minor.Patch.Build`.

* **Major.Minor.Patch** comes from the git tag (or the manual input).
* **Build** is the GitHub Actions run number — appended automatically, so every
  build is uniquely identifiable.

A tag `v1.3` therefore produces solution version `1.3.0.<build>`, e.g.
`1.3.0.42`. Tags may be `vX.Y` or `vX.Y.Z`.

## Cutting a release (with tag)

```pwsh
git tag v1.3.0
git push origin v1.3.0
```

Pushing a `v*` tag runs the workflow, which:

1. Builds the solution in Release and runs the in-process tests (Live tests
   excluded).
2. Produces `CeledonAutoNumber_1_3_0_<build>.zip` (unmanaged) and
   `CeledonAutoNumber_1_3_0_<build>_managed.zip`.
3. Uploads both as run artifacts.
4. Creates a **GitHub Release** for the tag with both zips attached.

Use the **managed** zip for downstream/production environments; the unmanaged
zip is for development environments only (it overwrites the sitemap — see
`Solutions/WARNING.MD`).

## Test build (no release)

Run the `release` workflow manually (**Actions → release → Run workflow**) with
a base version such as `1.3.0`. This builds and uploads the zips as run
artifacts **without** creating a tag or a Release — handy for verifying a build
before tagging.

## Custom action (cel_GenerateAutoNumber)

The on-demand action message `cel_GenerateAutoNumber`, its `GenerateAutoNumberAction`
plugin step (stage 40, synchronous) and the plugin type are **baked into the template
solution zips** under `Solutions/`, so importing the solution brings the action with it —
no per-environment setup needed.

The templates were produced by exporting the `CeledonAutoNumber` solution from a dev
environment. To regenerate them after changing solution components, re-export managed +
unmanaged and overwrite the files under `Solutions/`. `scripts/New-GenerateAutoNumberAction.ps1`
remains available to (re)create just the action message in an environment if needed.

## Local build

```pwsh
msbuild AutoNumber.sln /p:Configuration=Release
./scripts/Build-SolutionArtifacts.ps1 `
    -Version "1.3.0.0" `
    -AssemblyPath "AutoNumber\bin\Release\Celedon.AutoNumber.dll"
# -> artifacts/CeledonAutoNumber_1_3_0_0.zip (+ _managed.zip)
```
