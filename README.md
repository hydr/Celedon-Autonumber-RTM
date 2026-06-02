# Celedon Partners Dynamics CRM AutoNumber
Provides auto-numbering to Dynamics CRM.

> **Fork notice** — this is a crossvertise-maintained fork of
> [Ardalyst/Celedon-Autonumber-RTM](https://github.com/Ardalyst/Celedon-Autonumber-RTM).
> On top of the original plugin it adds: on-demand numbering via a custom action,
> bulk (CreateMultiple/UpdateMultiple) support, step-lifecycle coupling with a simple
> migration path, a GitHub Actions build/test pipeline, live Dataverse integration
> tests (see [`LiveTests.md`](LiveTests.md)), and a versioned solution release
> pipeline (see [`RELEASING.md`](RELEASING.md)). Usage details are in
> [`Documentation.md`](Documentation.md).

**Build status**

[![build](https://github.com/hydr/Celedon-Autonumber-RTM/actions/workflows/build.yml/badge.svg)](https://github.com/hydr/Celedon-Autonumber-RTM/actions/workflows/build.yml)

Releases are published from `v*` tags — download the managed/unmanaged solution
zips from the [Releases](https://github.com/hydr/Celedon-Autonumber-RTM/releases) page.

## How To Build
The following is required to build AutoNumber:

* [Microsoft Visual Studio 2015 or 2017](https://www.visualstudio.com/vs/older-downloads/)
* [CRM Developer Toolkit - by Jason Lattimer](https://github.com/jlattimer/CRMDeveloperExtensions)

> The current version builds against the Dynamics CRM 2016 - v6.0 SDK and .Net 4.0. You can [look here](https://blogs.msdn.microsoft.com/crm/2017/02/01/dynamics-365-sdk-backwards-compatibility/) for more information on SDK compatibilities. Since this solution does not connect to CRM Via alternative methods we do not need to update the connectivity support that changed in the later versions of CRM-Online for OAuth support.

## v1.4
* **On-demand number assignment** via the global custom action **`cel_GenerateAutoNumber`** —
  assign a number to a record from a classic workflow ("Perform Action"), Power Automate
  ("Perform an unbound action"), JavaScript, or plugin/Web-API code. Inputs `TargetEntity`,
  `TargetId`, optional `AutoNumberConfigId` / `AttributeName`; output `Number`. The regular
  trigger condition is bypassed, but an existing value is never overwritten.
* **Bulk operations optimized for `CreateMultiple` / `UpdateMultiple`** — a batch (e.g. 100 rows in
  one request) is numbered in a single plugin invocation: one lock + one counter increment for the
  whole batch instead of fanning out to ~4 service calls per record. Numbers stay unique and
  sequential; the counter advances by exactly the number of records assigned.
* **Step lifecycle coupled to the active state** — deactivating a `cel_autonumber` removes its
  plugin steps (so an inactive config costs nothing in the pipeline); any update of an active config
  (re)registers them. **Migrating an existing config to the new layout is just a re-save** — no
  deactivate/reactivate needed and no numbering gap. Migrate many at once with
  `scripts/Migrate-AutoNumberConfigs.ps1` (supports `-DryRun`).
* **Transactional guarantees hardened** — the counter is read *after* the row lock is acquired
  (inside the synchronous pre/post-operation transaction), so concurrent callers never get a
  duplicate and a rolled-back operation never skips a number.
* CI runs on the current (Node 24) GitHub Actions; live integration tests cover create/update,
  on-demand, bulk and the deactivate/reactivate lifecycle.

> Upgrading from a pre-v1.4 install: import the solution, then bring existing `cel_autonumber`
> configs onto the new step layout by re-saving each one (or run
> `scripts/Migrate-AutoNumberConfigs.ps1 -DryRun` to preview, then without `-DryRun`).

## v1.3
* Update plugin steps are now scoped with `filteringattributes` (trigger attribute,
  target attribute and conditional optionset), so the pipeline no longer loads the
  plugin on unrelated attribute changes. Multiple autonumber records on the same
  entity/event merge their filters into the shared step.
* Fixed `DeleteAutoNumber`: it was registered for the wrong pipeline stage and never
  removed the plugin step when the last autonumber record was deleted.
* Added a GitHub Actions build/test pipeline, live Dataverse integration tests
  ([`LiveTests.md`](LiveTests.md)) and a managed/unmanaged solution release pipeline
  ([`RELEASING.md`](RELEASING.md)).

## v1.2
> The plugin distributed with this version is *NOT* compatible with previous versions. You can import this Solution over the existing as an upgrade but you will need to convert the existing auto-number steps to the new plug-in in order to maintain support.

* Updated Code Formatting - *ReSharper* all the things!
* Refactored code to be *Thread Safe*
* Packaged Solution is exported for v8.0 (2016 RTM) - this means support for the v1.2 Solution is supported in 8.x + (Including 9.0) versions of CRM.
* SDK Version is set to 6.0.0
* Added back the test cases and converted to NUnit so that travis-ci can build

## v1.1:
* Supports custom prefix and suffix
* Configurable number of digits
* All parameters except the entityname and attributename can be modified at any time
* Supports multiple autonumbers on the same entity
* Generated numbers guaranteed to be unique, even in load balanced environments, including CRM Online
* Displays a live preview of the autonumber on the config form
* Validates entity and attributes are valid, and that there are no duplicate entries
* Supports conditional number generation (eg: allows different account types (or whatever) to get different number formats)
* Supports Activating/Deactivating AutoNumbers
* Allows runtime parameters to be entered into the Prefix and Suffix fields (See below for instructions)
* Runtime parameters now support looking up to parent record values
* Supports nested conditional parameters ie: "else if" conditions
* NEW: Supports 0 digit numbers (allows fully custom calculated fields, without adding any number)
* NEW: Added ability to generate random strings
* NEW: Can now trigger generation on either a Create OR Update event of a record

@See Documentation.md for usage.
