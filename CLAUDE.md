# CLAUDE.md

Guidance for working in this repo. Celedon AutoNumber is a Dynamics 365 / Dataverse plugin
solution (crossvertise fork of Ardalyst/Celedon-Autonumber-RTM) that assigns formatted sequential
numbers to records.

## Build & test

- **.NET Framework 4.6.2**, classic-style `.csproj` with **explicit `<Compile Include=...>`** entries —
  when you add a new `.cs` file to `AutoNumber/`, you must add it to `AutoNumber/AutoNumber.csproj`.
- Build: `msbuild AutoNumber.sln /p:Configuration=Release /p:Platform="Any CPU"` (VS MSBuild; the repo
  also restores via `nuget restore AutoNumber.sln`). Packages are committed under `..\packages`.
- Tests are **NUnit** in `AutoNumber.Tests`. Two categories:
  - In-process (mocked `FakeOrganizationService` + `PluginHarness`): run with
    `nunit3-console AutoNumber.Tests\bin\<cfg>\AutoNumber.Tests.dll --where "cat != Live"`.
  - Live (`[Category("Live")]`, real Dataverse): `--where "cat == Live"`, gated by `DATAVERSE_URL` +
    `DATAVERSE_TOKEN` (else Inconclusive). See `LiveTests.md`.
- CI: `.github/workflows/build.yml` (push/PR), `live-tests.yml` (manual, deploys assembly then runs
  live tests), `release.yml` (`v*` tag → managed+unmanaged zips + GitHub Release). See `RELEASING.md`.

## Architecture

Plugins derive from `CeledonPlugin`; its `Execute(IServiceProvider)` dispatches to a handler only on
an **exact (Stage, MessageName, PrimaryEntity)** match registered via `RegisterEvent(...)` in the ctor
(empty entity = wildcard, used for global custom actions).

- `ValidateAutoNumber` — PreValidation on Create of `cel_autonumber`; validates the config (target must
  be a **String/Memo** attribute, etc.).
- `CreateAutoNumber` — PostOperation on Create of `cel_autonumber`; **dynamically registers** the
  `GetNextAutoNumber` steps on the target entity. Registers BOTH the single step (`Create`/`Update`)
  and the bulk step (`CreateMultiple`/`UpdateMultiple`). Reusable entry point: `RegisterSteps(context, config)`.
- `DeleteAutoNumber` — **PostOperation** on Delete of `cel_autonumber`; removes/recomputes the steps.
  Reusable: `RemoveOrRecomputeSteps(...)`.
- `UpdateAutoNumber` — PostOperation on Update of `cel_autonumber` (filtered on config fields, NOT the
  counter fields); couples step lifecycle to the active state: any update of an active config
  re-registers steps; deactivate removes them. This is the **migration path** (a re-save migrates a
  config to the current step layout).
- `GetNextAutoNumber` — PreOperation on the target entity's Create/Update + their `*Multiple` variants.
  Builds `prefix + zero-padded cel_nextnumber + suffix` (`FormatAutoNumber`), with `{token}` runtime
  parameters resolved by `Extensions.ReplaceParameters`. Bulk: one lock + one increment per batch.
- `GenerateAutoNumberAction` — handler for the global custom action `cel_GenerateAutoNumber`
  (on-demand numbering from classic workflows, Power Automate, code).

Dynamic step names: `CeledonPartners.AutoNumber.<entity>`[` Update`][` (<Message>Multiple)`].

## Gotchas (hard-won)

- **DeleteAutoNumber / UpdateAutoNumber must be PostOperation (stage 40)** — that's where the
  registered steps fire, and the deleted/updated state is final. Stage 30 cannot host a registered step.
- **Number uniqueness/atomicity**: the plugin locks the `cel_autonumber` row (`cel_preview="555"`)
  **before reading** `cel_nextnumber`, all inside the synchronous pre/post-operation transaction. Keep
  it that way — read-before-lock or async would allow duplicates; out-of-transaction would allow gaps.
- **Custom action arguments must be primitive (String)** — `EntityReference` args fail with an
  "Error generating UiData" on message creation. The action takes GUIDs/logical names as strings.
- **`IPluginExecutionContext` (SDK 9.0.2.59) lacks `PreEntityImagesCollection`** — read it via
  reflection for the UpdateMultiple per-record pre-images.
- The target attribute must be **text**; trigger/conditional attributes only gate when a number is
  generated.

## Environments

- Live tests run against a **dedicated dev Dataverse sandbox**, configured via the repo variables
  `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `DATAVERSE_URL` and the `live-tests` GitHub environment
  (passwordless OIDC). See `LiveTests.md`.
- Any other Dataverse environment may be **shared or production** with live numbering data. Do **not**
  deploy in-development assemblies to a shared/production environment without explicit approval;
  managed-solution downgrades are not possible, so plan rollbacks. Releases deploy via
  `pac solution import` (or the token-based scripts below); after import, migrate existing configs with
  `scripts/Migrate-AutoNumberConfigs.ps1` (`-DryRun` first).

## Scripts (`scripts/`, PowerShell, OIDC/az auth — no secrets/URLs hard-coded)

- `Deploy-PluginAssembly.ps1` — deploy/update the plugin assembly via Web API.
- `Build-SolutionArtifacts.ps1` — produce versioned managed+unmanaged zips (swaps the built DLL into
  the `Solutions/` templates, stamps the version).
- `New-GenerateAutoNumberAction.ps1` — create the `cel_GenerateAutoNumber` custom-action message.
- `Migrate-AutoNumberConfigs.ps1` — migrate existing configs to the current step layout via one
  re-save each (`-DryRun` for a read-only preview).

## Conventions

- Don't commit/push or deploy unless asked. Branch off `master` for changes; cut releases with `v*` tags.
- Keep `Documentation.md` (usage), `LiveTests.md`, `RELEASING.md`, and the `README.md` version history
  in sync with behavioural changes.
