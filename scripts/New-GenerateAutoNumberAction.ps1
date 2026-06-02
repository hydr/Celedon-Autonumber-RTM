<#
.SYNOPSIS
    Creates (and activates) the global custom action message cel_GenerateAutoNumber in Dataverse.

.DESCRIPTION
    The on-demand numbering handler (Celedon.GenerateAutoNumberAction) is registered against the
    custom-action message `cel_GenerateAutoNumber`. This script defines that message: a global
    Process action (category 3, type 1, primaryentity 'none') whose input/output arguments are
    declared via XAML, then activates it (statecode 1) so the platform generates the SdkMessage
    and request/response field metadata.

    Idempotent: an existing cel_GenerateAutoNumber action is left untouched.

    Arguments (all String — EntityReference args trigger a UiData generation error on create):
      In  TargetEntity       (String, required) — logical name of the record's entity
      In  TargetId           (String, required) — GUID of the record to assign the number to
      In  AutoNumberConfigId (String, optional) — GUID of a specific cel_autonumber definition
      In  AttributeName      (String, optional) — fallback when no config id is given
      Out Number             (String)           — the generated number

    Auth: a bearer token for the environment (OIDC/az). Pass -AccessToken or rely on az.
    No environment URL or secret is hard-coded — supply -EnvUrl or set DATAVERSE_URL.

.PARAMETER EnvUrl
    Dataverse environment URL. Falls back to the DATAVERSE_URL environment variable.

.PARAMETER AccessToken
    Pre-obtained bearer token. If omitted, acquired via `az account get-access-token`.

.EXAMPLE
    .\scripts\New-GenerateAutoNumberAction.ps1 -EnvUrl $env:DATAVERSE_URL -AccessToken $env:DATAVERSE_TOKEN
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$EnvUrl = $env:DATAVERSE_URL,

    [Parameter(Mandatory = $false)]
    [string]$AccessToken,

    # Solution whose publisher prefix forms the message name. The CeledonAutoNumber
    # publisher (celedonpartners) prefix is 'cel', so the message becomes cel_GenerateAutoNumber.
    [Parameter(Mandatory = $false)]
    [string]$SolutionName = 'CeledonAutoNumber'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($EnvUrl)) {
    throw "EnvUrl is required (pass -EnvUrl or set DATAVERSE_URL)."
}
$EnvUrl = $EnvUrl.TrimEnd('/')

if ([string]::IsNullOrWhiteSpace($AccessToken)) {
    $AccessToken = az account get-access-token --resource $EnvUrl --query accessToken -o tsv
    if ([string]::IsNullOrWhiteSpace($AccessToken)) {
        throw "Could not acquire a token. Run: az login  (and az account get-access-token --resource $EnvUrl)"
    }
}

$h = @{
    Authorization      = "Bearer $AccessToken"
    Accept             = 'application/json'
    'OData-Version'    = '4.0'
    'OData-MaxVersion' = '4.0'
    'Content-Type'     = 'application/json'
}

# Action definition ───────────────────────────────────────────────────────────
# UniqueName has NO prefix — the platform prepends the solution publisher prefix
# (celedonpartners => 'cel'), producing the message name 'cel_GenerateAutoNumber'.
$action = @{
    UniqueName = 'GenerateAutoNumber'
    Name       = 'Generate AutoNumber'
    Inputs     = @(
        @{ Name = 'TargetEntity';       Type = 'String'; Required = $true }
        @{ Name = 'TargetId';           Type = 'String'; Required = $true }
        @{ Name = 'AutoNumberConfigId'; Type = 'String'; Required = $false }
        @{ Name = 'AttributeName';      Type = 'String'; Required = $false }
    )
    Outputs    = @(
        @{ Name = 'Number'; Type = 'String' }
    )
}

function Get-XamlTypeArg([string]$type) {
    switch ($type) {
        'String'          { return 'x:String' }
        'Boolean'         { return 'x:Boolean' }
        'Int32'           { return 'x:Int32' }
        'Int64'           { return 'x:Int64' }
        'EntityReference' { return 'mxs:EntityReference' }
        default           { throw "Unsupported argument type: $type" }
    }
}

function Build-Xaml([hashtable]$action) {
    $className = 'XrmWorkflow' + ([Guid]::NewGuid().ToString('N'))

    $props = New-Object System.Text.StringBuilder
    [void]$props.Append('<x:Property Name="InputEntities" Type="InArgument(scg:IDictionary(x:String, mxs:Entity))" />')
    [void]$props.Append('<x:Property Name="CreatedEntities" Type="InArgument(scg:IDictionary(x:String, mxs:Entity))" />')

    foreach ($i in $action.Inputs) {
        $ta = Get-XamlTypeArg $i.Type
        $required = if ($i.Required) { 'True' } else { 'False' }
        [void]$props.Append(@"
<x:Property Name="$($i.Name)" Type="InArgument($ta)"><x:Property.Attributes><mxsw:ArgumentRequiredAttribute Value="$required" /><mxsw:ArgumentTargetAttribute Value="False" /><mxsw:ArgumentDescriptionAttribute Value="$($i.Name)" /><mxsw:ArgumentDirectionAttribute Value="Input" /></x:Property.Attributes></x:Property>
"@)
    }
    foreach ($o in $action.Outputs) {
        $ta = Get-XamlTypeArg $o.Type
        [void]$props.Append(@"
<x:Property Name="$($o.Name)" Type="OutArgument($ta)"><x:Property.Attributes><mxsw:ArgumentRequiredAttribute Value="False" /><mxsw:ArgumentTargetAttribute Value="False" /><mxsw:ArgumentDescriptionAttribute Value="$($o.Name)" /><mxsw:ArgumentDirectionAttribute Value="Output" /></x:Property.Attributes></x:Property>
"@)
    }

    return @"
<?xml version="1.0" encoding="utf-16"?><Activity x:Class="$className" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities" xmlns:mva="clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" xmlns:mxs="clr-namespace:Microsoft.Xrm.Sdk;assembly=Microsoft.Xrm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" xmlns:mxsw="clr-namespace:Microsoft.Xrm.Sdk.Workflow;assembly=Microsoft.Xrm.Sdk.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" xmlns:mxswa="clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" xmlns:scg="clr-namespace:System.Collections.Generic;assembly=mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" xmlns:srs="clr-namespace:System.Runtime.Serialization;assembly=System.Runtime.Serialization, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" xmlns:this="clr-namespace:" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><x:Members>$($props.ToString())</x:Members><this:$className.InputEntities><InArgument x:TypeArguments="scg:IDictionary(x:String, mxs:Entity)" /></this:$className.InputEntities><this:$className.CreatedEntities><InArgument x:TypeArguments="scg:IDictionary(x:String, mxs:Entity)" /></this:$className.CreatedEntities><mva:VisualBasic.Settings>Assembly references and imported namespaces for internal implementation</mva:VisualBasic.Settings><mxswa:Workflow /></Activity>
"@
}

# Main ──────────────────────────────────────────────────────────────────────────
Write-Host "Environment: $EnvUrl"

# Use the organization's base language (hard-coding 1033 fails on non-English orgs).
$org = Invoke-RestMethod -Method GET -Headers $h -Uri "$EnvUrl/api/data/v9.2/organizations?`$select=languagecode&`$top=1"
$baseLcid = [int]$org.value[0].languagecode
Write-Host "Base language: $baseLcid"

# Resolve the solution publisher prefix to compute the final message name.
$sol = Invoke-RestMethod -Method GET -Headers $h `
    -Uri "$EnvUrl/api/data/v9.2/solutions?`$filter=uniquename eq '$SolutionName'&`$select=solutionid&`$expand=publisherid(`$select=customizationprefix)"
if (@($sol.value).Count -eq 0) { throw "Solution '$SolutionName' not found." }
$prefix = $sol.value[0].publisherid.customizationprefix
$messageName = "${prefix}_$($action.UniqueName)"
Write-Host "Solution: $SolutionName  ->  message name: $messageName"

$existingMsg = Invoke-RestMethod -Method GET -Headers $h `
    -Uri "$EnvUrl/api/data/v9.2/sdkmessages?`$filter=name eq '$messageName'&`$select=sdkmessageid"
if (@($existingMsg.value).Count -gt 0) {
    Write-Host ("[=] Message '{0}' already exists (id={1}). Skipped." -f $messageName, $existingMsg.value[0].sdkmessageid) -ForegroundColor Yellow
    return
}

$body = @{
    category      = 3        # Action
    type          = 1        # Definition
    mode          = 0
    scope         = 4        # Organization (global)
    primaryentity = 'none'
    name          = $action.Name
    uniquename    = $action.UniqueName
    languagecode  = $baseLcid
    xaml          = (Build-Xaml $action)
} | ConvertTo-Json -Depth 5

# Create inside the solution so its publisher prefix is applied to the generated message.
$createHeaders = $h.Clone(); $createHeaders['Prefer'] = 'return=representation'; $createHeaders['MSCRM.SolutionUniqueName'] = $SolutionName
$resp = Invoke-RestMethod -Method POST -Uri "$EnvUrl/api/data/v9.2/workflows" -Headers $createHeaders -Body $body
$wfId = $resp.workflowid
Write-Host ("[+] created, workflowid={0}" -f $wfId) -ForegroundColor Green

$patchHeaders = $h.Clone(); $patchHeaders['If-Match'] = '*'
$activate = @{ statecode = 1; statuscode = 2 } | ConvertTo-Json -Compress
Invoke-RestMethod -Method PATCH -Uri "$EnvUrl/api/data/v9.2/workflows($wfId)" -Headers $patchHeaders -Body $activate | Out-Null
Write-Host "[+] activated (statecode=1)." -ForegroundColor Green

$verify = Invoke-RestMethod -Method GET -Headers $h -Uri "$EnvUrl/api/data/v9.2/sdkmessages?`$filter=name eq '$messageName'&`$select=sdkmessageid"
if (@($verify.value).Count -eq 0) { throw "Activation did not produce the SdkMessage '$messageName'." }
Write-Host ("[+] SdkMessage '{0}' created (id={1})." -f $messageName, $verify.value[0].sdkmessageid) -ForegroundColor Green
