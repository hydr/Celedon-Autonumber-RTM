# Celedon Partners Dynamics CRM AutoNumber
Provides auto-numbering to Dynamics CRM.

> **Fork notice** — this is a crossvertise-maintained fork of
> [Ardalyst/Celedon-Autonumber-RTM](https://github.com/Ardalyst/Celedon-Autonumber-RTM).
> It adds a GitHub Actions build/test pipeline, live Dataverse integration tests
> (see [`LiveTests.md`](LiveTests.md)), and a versioned solution release pipeline
> (see [`RELEASING.md`](RELEASING.md)).

**Build status**

[![build](https://github.com/hydr/Celedon-Autonumber-RTM/actions/workflows/build.yml/badge.svg)](https://github.com/hydr/Celedon-Autonumber-RTM/actions/workflows/build.yml)

Releases are published from `v*` tags — download the managed/unmanaged solution
zips from the [Releases](https://github.com/hydr/Celedon-Autonumber-RTM/releases) page.

## How To Build
The following is required to build AutoNumber:

* [Microsoft Visual Studio 2015 or 2017](https://www.visualstudio.com/vs/older-downloads/)
* [CRM Developer Toolkit - by Jason Lattimer](https://github.com/jlattimer/CRMDeveloperExtensions)

> The current version builds against the Dynamics CRM 2016 - v6.0 SDK and .Net 4.0. You can [look here](https://blogs.msdn.microsoft.com/crm/2017/02/01/dynamics-365-sdk-backwards-compatibility/) for more information on SDK compatibilities. Since this solution does not connect to CRM Via alternative methods we do not need to update the connectivity support that changed in the later versions of CRM-Online for OAuth support.

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
