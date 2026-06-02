<#
.SYNOPSIS
    Migrates existing cel_autonumber configs onto the current step layout by triggering a single
    harmless Update on each one — exactly as if you re-saved the record by hand.

.DESCRIPTION
    The UpdateAutoNumber plugin (re)registers the single + bulk (CreateMultiple/UpdateMultiple)
    steps on any Update of an ACTIVE cel_autonumber, idempotently. So this migration does NOT
    register anything itself — it simply re-writes one config attribute (cel_attributename to its
    current value) on each active config, which fires the plugin and re-syncs the steps. The config
    stays active throughout (no deactivation gap), and the migration logic has a single source of
    truth: the plugin.

    Only ACTIVE configs are touched. Inactive configs are left untouched (they have no live steps).

    Prerequisite: the updated solution/assembly (with the UpdateAutoNumber step) must already be
    imported into the environment. The script verifies the step exists and aborts otherwise.

    Auth: a bearer token for the environment (OIDC/az). No environment URL or secret is hard-coded.

.PARAMETER EnvUrl
    Dataverse environment URL. Falls back to the DATAVERSE_URL environment variable.

.PARAMETER AccessToken
    Pre-obtained bearer token. If omitted, acquired via `az account get-access-token`.

.PARAMETER DryRun
    Report which configs would be cycled, without writing anything (read-only).

.PARAMETER InactiveStatusCode
    statuscode to set when deactivating (default 2 = Inactive for a standard custom entity).

.PARAMETER ActiveStatusCode
    statuscode to set when reactivating (default 1 = Active).

.EXAMPLE
    ./scripts/Migrate-AutoNumberConfigs.ps1 -EnvUrl $env:DATAVERSE_URL -DryRun
    ./scripts/Migrate-AutoNumberConfigs.ps1 -EnvUrl $env:DATAVERSE_URL
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)] [string]$EnvUrl = $env:DATAVERSE_URL,
    [Parameter(Mandatory = $false)] [string]$AccessToken,
    [Parameter(Mandatory = $false)] [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($EnvUrl)) { throw "EnvUrl is required (pass -EnvUrl or set DATAVERSE_URL)." }
$EnvUrl = $EnvUrl.TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($AccessToken)) {
    $AccessToken = az account get-access-token --resource $EnvUrl --query accessToken -o tsv
    if ([string]::IsNullOrWhiteSpace($AccessToken)) { throw "Could not acquire a token. Run az login first." }
}

$h = @{ Authorization = "Bearer $AccessToken"; Accept = 'application/json'; 'OData-Version' = '4.0'; 'OData-MaxVersion' = '4.0'; 'Content-Type' = 'application/json' }
function Dv-Get([string]$q) { return Invoke-RestMethod -Method GET -Uri "$EnvUrl/api/data/v9.2/$q" -Headers $h }
function Dv-Touch([string]$id, [string]$attributeName) {
    # Re-write one config attribute to its current value — a no-op-value update that includes a
    # filtered attribute, so the UpdateAutoNumber step fires and re-syncs the steps.
    $ph = $h.Clone(); $ph['If-Match'] = '*'
    $body = @{ cel_attributename = $attributeName } | ConvertTo-Json -Compress
    Invoke-RestMethod -Method PATCH -Uri "$EnvUrl/api/data/v9.2/cel_autonumbers($id)" -Headers $ph -Body $body | Out-Null
}

Write-Host "Environment: $EnvUrl"

# The lifecycle step must exist, otherwise toggling status would do nothing useful.
$step = Dv-Get "sdkmessageprocessingsteps?`$filter=name eq 'Celedon.UpdateAutoNumber: Update of cel_autonumber'&`$select=sdkmessageprocessingstepid"
if (@($step.value).Count -eq 0) {
    throw "The 'Celedon.UpdateAutoNumber' step is not registered. Import the updated AutoNumber solution first."
}

$configs = Dv-Get "cel_autonumbers?`$filter=statecode eq 0&`$select=cel_autonumberid,cel_entityname,cel_attributename"
$done = 0
foreach ($c in $configs.value) {
    $label = "{0} ({1}.{2})" -f $c.cel_autonumberid, $c.cel_entityname, $c.cel_attributename
    if ($DryRun) {
        Write-Host "  [~] would re-save (touch) $label" -ForegroundColor Cyan
        $done++
        continue
    }

    Write-Host "  [*] $label : re-saving..." -NoNewline
    Dv-Touch $c.cel_autonumberid $c.cel_attributename         # -> UpdateAutoNumber registers single + bulk
    Write-Host " done" -ForegroundColor Green
    $done++
}

Write-Host ""
$verb = if ($DryRun) { "would migrate" } else { "migrated" }
Write-Host ("Done. {0} {1} active config(s) via a single update." -f $verb, $done) -ForegroundColor Green
