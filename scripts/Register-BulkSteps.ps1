<#
.SYNOPSIS
    Registers the CreateMultiple / UpdateMultiple plugin steps for EXISTING cel_autonumber configs.

.DESCRIPTION
    New cel_autonumber records get their bulk (*Multiple) step automatically from CreateAutoNumber.
    This one-time migration adds the matching bulk step for configs that already existed before the
    bulk optimization shipped. It mirrors CreateAutoNumber.RegisterOrMergeStep:

      * step name  = "CeledonPartners.AutoNumber.<entity>"[ + " Update"] + " (<Message>Multiple)"
      * stage      = 20 (PreOperation), synchronous, rank 1
      * Update     -> filteringattributes (trigger + target + conditional) and a "Targets" PreImage
      * skipped (not fatal) when the entity has no *Multiple message filter
      * idempotent: an existing bulk step is left alone (its filter is merged for Update)

    Auth: a bearer token for the environment (OIDC/az). No environment URL or secret is hard-coded.

.PARAMETER EnvUrl
    Dataverse environment URL. Falls back to the DATAVERSE_URL environment variable.

.PARAMETER AccessToken
    Pre-obtained bearer token. If omitted, acquired via `az account get-access-token`.

.EXAMPLE
    ./scripts/Register-BulkSteps.ps1 -EnvUrl $env:DATAVERSE_URL -AccessToken $env:DATAVERSE_TOKEN
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)] [string]$EnvUrl = $env:DATAVERSE_URL,
    [Parameter(Mandatory = $false)] [string]$AccessToken,
    # Report what would change without writing anything (read-only).
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
function Dv-Post([string]$set, $body) {
    $ch = $h.Clone(); $ch['Prefer'] = 'return=representation'
    return Invoke-RestMethod -Method POST -Uri "$EnvUrl/api/data/v9.2/$set" -Headers $ch -Body ($body | ConvertTo-Json -Depth 6)
}
function Dv-Patch([string]$set, [string]$id, $body) {
    $ph = $h.Clone(); $ph['If-Match'] = '*'
    Invoke-RestMethod -Method PATCH -Uri "$EnvUrl/api/data/v9.2/$set($id)" -Headers $ph -Body ($body | ConvertTo-Json -Depth 6) | Out-Null
}

Write-Host "Environment: $EnvUrl"

$pluginType = Dv-Get "plugintypes?`$filter=typename eq 'Celedon.GetNextAutoNumber'&`$select=plugintypeid"
if (@($pluginType.value).Count -eq 0) { throw "Plugin type 'Celedon.GetNextAutoNumber' not found — deploy the assembly first." }
$pluginTypeId = $pluginType.value[0].plugintypeid

$configs = Dv-Get "cel_autonumbers?`$filter=statecode eq 0&`$select=cel_entityname,cel_attributename,cel_triggerevent,cel_triggerattribute,cel_conditionaloptionset"
$created = 0; $merged = 0; $skipped = 0

foreach ($c in $configs.value) {
    $entity = $c.cel_entityname
    $isUpdate = ($c.cel_triggerevent -eq 1)
    $eventName = if ($isUpdate) { 'Update' } else { 'Create' }
    $message = "${eventName}Multiple"
    $targetAttr = $c.cel_attributename

    $stepName = "CeledonPartners.AutoNumber.$entity"
    if ($isUpdate) { $stepName += ' Update' }
    $stepName += " ($message)"

    Write-Host "=== $stepName ==="

    # filtering attributes (Update only): trigger + target + conditional
    $filterAttrs = @()
    if ($isUpdate) {
        foreach ($a in @($c.cel_triggerattribute, $targetAttr, $c.cel_conditionaloptionset)) {
            if (-not [string]::IsNullOrWhiteSpace($a) -and $filterAttrs -notcontains $a) { $filterAttrs += $a }
        }
    }

    $existing = Dv-Get "sdkmessageprocessingsteps?`$filter=name eq '$stepName'&`$select=sdkmessageprocessingstepid,filteringattributes"
    if (@($existing.value).Count -gt 0) {
        if ($isUpdate) {
            $have = @(); if ($existing.value[0].filteringattributes) { $have = $existing.value[0].filteringattributes.Split(',') | ForEach-Object { $_.Trim() } }
            $union = ($have + $filterAttrs | Where-Object { $_ } | Select-Object -Unique)
            if ($DryRun) {
                Write-Host ("  [~] would merge filtering attributes -> {0}" -f ($union -join ',')) -ForegroundColor Cyan
            } else {
                Dv-Patch 'sdkmessageprocessingsteps' $existing.value[0].sdkmessageprocessingstepid @{ filteringattributes = ($union -join ',') }
                Write-Host "  [~] exists; merged filtering attributes" -ForegroundColor Yellow
            }
            $merged++
        } else {
            Write-Host "  [=] exists; skipped" -ForegroundColor Yellow; $skipped++
        }
        continue
    }

    $msg = Dv-Get "sdkmessages?`$filter=name eq '$message'&`$select=sdkmessageid"
    if (@($msg.value).Count -eq 0) { Write-Host "  [!] message '$message' not found; skipped" -ForegroundColor Red; $skipped++; continue }
    $messageId = $msg.value[0].sdkmessageid

    $flt = Dv-Get "sdkmessagefilters?`$filter=_sdkmessageid_value eq $messageId and primaryobjecttypecode eq '$entity'&`$select=sdkmessagefilterid"
    if (@($flt.value).Count -eq 0) { Write-Host "  [!] no '$message' filter for '$entity'; skipped (entity may not support bulk)" -ForegroundColor Red; $skipped++; continue }
    $filterId = $flt.value[0].sdkmessagefilterid

    $config = "{`"EntityName`":`"$entity`",`"EventName`":`"$eventName`"}"
    $step = @{
        name                            = $stepName
        description                     = $stepName
        stage                           = 20
        mode                            = 0
        rank                            = 1
        configuration                   = $config
        'plugintypeid@odata.bind'       = "/plugintypes($pluginTypeId)"
        'sdkmessageid@odata.bind'       = "/sdkmessages($messageId)"
        'sdkmessagefilterid@odata.bind' = "/sdkmessagefilters($filterId)"
    }
    if ($isUpdate -and $filterAttrs.Count -gt 0) { $step['filteringattributes'] = ($filterAttrs -join ',') }

    if ($DryRun) {
        $imgNote = if ($isUpdate) { " + PreImage(Targets attributes='$targetAttr')" } else { "" }
        Write-Host ("  [+] WOULD create step '{0}' (stage 20, filter='{1}'){2}" -f $stepName, ($filterAttrs -join ','), $imgNote) -ForegroundColor Cyan
        $created++
        continue
    }

    $resp = Dv-Post 'sdkmessageprocessingsteps' $step
    $stepId = $resp.sdkmessageprocessingstepid
    Write-Host "  [+] created step $stepId" -ForegroundColor Green

    if ($isUpdate) {
        $image = @{
            'sdkmessageprocessingstepid@odata.bind' = "/sdkmessageprocessingsteps($stepId)"
            imagetype           = 0          # PreImage
            messagepropertyname = 'Targets'  # bulk message carries records in Targets
            name                = 'Image'
            entityalias         = 'Image'
            attributes          = $targetAttr
        }
        Dv-Post 'sdkmessageprocessingstepimages' $image | Out-Null
        Write-Host "  [+] created PreImage (Targets)" -ForegroundColor Green
    }
    $created++
}

Write-Host ""
Write-Host ("Done. Created: {0}, Merged: {1}, Skipped: {2}" -f $created, $merged, $skipped) -ForegroundColor Green
