<#
.SYNOPSIS
    Deploys a plugin assembly to Dynamics 365 / Dataverse.

.DESCRIPTION
    Registers a new plugin assembly or updates an existing one in Dynamics 365 / Dataverse
    using the Web API. Self-contained — no external PowerShell modules required.

    Supports both interactive authentication (device code flow) and service principal
    authentication (CI/CD pipelines). Existing assemblies are updated in place (content
    and version); new assemblies are created with Database source type and Sandbox isolation.

.PARAMETER AssemblyPath
    Path to the built plugin DLL to deploy.

.PARAMETER CrmUrl
    The URL of your Dynamics 365 / Dataverse environment.
    Falls back to CRM_URL environment variable.

.PARAMETER Username
    Optional login hint for interactive auth. Pre-fills the username in the
    browser sign-in popup. Has no effect when using service principal auth.

.PARAMETER ClientId
    Azure AD App Client ID for service principal authentication.
    Falls back to CLIENT_ID environment variable.
    Omit for interactive device-code login.

.PARAMETER ClientSecret
    Azure AD App Client Secret for service principal authentication.
    Falls back to CLIENT_SECRET environment variable.

.PARAMETER TenantId
    Azure AD Tenant ID. Required for service principal authentication.
    Falls back to TENANT_ID environment variable.
    For interactive auth, 'organizations' is used automatically.

.PARAMETER AccessToken
    Pre-obtained OAuth2 access token for Dataverse (e.g. from 'az account get-access-token').
    When provided, all other auth parameters are ignored.
    Intended for CI/CD pipelines using federated/OIDC authentication.

.PARAMETER SolutionName
    Optional Dataverse solution unique name. When provided, the assembly is added
    to this solution after being created or updated.

.PARAMETER WhatIf
    When set, shows what would be done without making changes.

.EXAMPLE
    # Local dev — deploy the AutoNumber assembly (opens browser sign-in)
    .\scripts\Deploy-PluginAssembly.ps1 `
        -AssemblyPath "AutoNumber\bin\Release\Celedon.AutoNumber.dll" `
        -CrmUrl "https://orgxxxxxxxx.crm4.dynamics.com" `
        -Username "you@example.com"

.EXAMPLE
    # CI/CD — deploy with a pre-obtained OIDC access token (see .github/workflows/live-tests.yml)
    .\scripts\Deploy-PluginAssembly.ps1 `
        -AssemblyPath "AutoNumber\bin\Release\Celedon.AutoNumber.dll" `
        -CrmUrl $env:DATAVERSE_URL -AccessToken $env:DATAVERSE_TOKEN

.EXAMPLE
    # Dry run
    .\scripts\Deploy-PluginAssembly.ps1 `
        -AssemblyPath "AutoNumber\bin\Release\Celedon.AutoNumber.dll" `
        -CrmUrl "https://orgxxxxxxxx.crm4.dynamics.com" -WhatIf

.NOTES
    No external modules required. Works with PowerShell 5.1+ and PowerShell 7+.
    Uses OAuth2 device code flow for interactive auth and Dataverse Web API for all operations.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [string]$AssemblyPath,

    [Parameter(Mandatory = $false)]
    [string]$CrmUrl = $env:CRM_URL,

    [Parameter(Mandatory = $false)]
    [string]$Username,

    [Parameter(Mandatory = $false)]
    [string]$ClientId = $env:CLIENT_ID,

    [Parameter(Mandatory = $false)]
    [string]$ClientSecret = $env:CLIENT_SECRET,

    [Parameter(Mandatory = $false)]
    [string]$TenantId = $env:TENANT_ID,

    [Parameter(Mandatory = $false)]
    [string]$AccessToken,

    [Parameter(Mandatory = $false)]
    [string]$SolutionName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Constants ────────────────────────────────────────────────────────────────

$ApiVersion = "v9.2"

# Well-known first-party Dynamics 365 client ID (supports redirect to localhost)
$InteractiveClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d"
$RedirectUri = "http://localhost:8400"

# ── Authentication ───────────────────────────────────────────────────────────

function Get-AccessToken {
    param(
        [string]$crmUrl,
        [string]$username,
        [string]$clientId,
        [string]$clientSecret,
        [string]$tenantId
    )

    $scope = "$crmUrl/.default"
    $useServicePrincipal = -not [string]::IsNullOrWhiteSpace($clientId) -and -not [string]::IsNullOrWhiteSpace($clientSecret)

    if ($useServicePrincipal) {
        if ([string]::IsNullOrWhiteSpace($tenantId)) {
            throw "TenantId is required for service principal authentication."
        }

        Write-Host "Authenticating with service principal..."
        $tokenUrl = "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token"
        $body = @{
            grant_type    = "client_credentials"
            client_id     = $clientId
            client_secret = $clientSecret
            scope         = $scope
        }

        $response = Invoke-RestMethod -Method Post -Uri $tokenUrl -Body $body -ContentType "application/x-www-form-urlencoded"
        return $response.access_token
    }
    else {
        # Try Azure CLI first — silent, no browser needed if 'az login' is active
        try {
            $azToken = (& az account get-access-token --resource $crmUrl --query accessToken -o tsv 2>$null)
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($azToken)) {
                Write-Host "Using Azure CLI token (current Windows user)."
                return $azToken.Trim()
            }
        } catch { }

        # Fall back to authorization code flow with PKCE — opens browser popup
        $authority = "https://login.microsoftonline.com/organizations"
        if (-not [string]::IsNullOrWhiteSpace($tenantId)) {
            $authority = "https://login.microsoftonline.com/$tenantId"
        }

        # Generate PKCE code verifier and challenge
        $codeVerifierBytes = [byte[]]::new(32)
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($codeVerifierBytes)
        $codeVerifier = [Convert]::ToBase64String($codeVerifierBytes) -replace '\+','-' -replace '/','_' -replace '='
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        $challengeBytes = $sha256.ComputeHash([System.Text.Encoding]::ASCII.GetBytes($codeVerifier))
        $codeChallenge = [Convert]::ToBase64String($challengeBytes) -replace '\+','-' -replace '/','_' -replace '='

        # Build authorize URL
        $state = [guid]::NewGuid().ToString("N")
        $authorizeUrl = "$authority/oauth2/v2.0/authorize?" + `
            "client_id=$InteractiveClientId" + `
            "&response_type=code" + `
            "&redirect_uri=$([uri]::EscapeDataString($RedirectUri))" + `
            "&scope=$([uri]::EscapeDataString($scope + ' offline_access openid'))" + `
            "&state=$state" + `
            "&code_challenge=$codeChallenge" + `
            "&code_challenge_method=S256" + `
            "&prompt=select_account"

        if (-not [string]::IsNullOrWhiteSpace($username)) {
            $authorizeUrl += "&login_hint=$([uri]::EscapeDataString($username))"
        }

        # Start localhost listener
        $listener = [System.Net.HttpListener]::new()
        $listener.Prefixes.Add("$RedirectUri/")
        $listener.Start()

        try {
            # Open browser
            Write-Host "Opening browser for sign-in..."
            Start-Process $authorizeUrl

            # Wait for the redirect (timeout 120s)
            $contextTask = $listener.GetContextAsync()
            if (-not $contextTask.Wait(120000)) {
                throw "Browser sign-in timed out after 120 seconds."
            }
            $context = $contextTask.Result

            # Parse the authorization code from the query string
            $query = $context.Request.QueryString
            $returnedState = $query["state"]
            $code = $query["code"]
            $error_ = $query["error"]

            # Send a response to the browser
            $responseHtml = "<html><body><h3>Authentication complete. You can close this tab.</h3></body></html>"
            $buffer = [System.Text.Encoding]::UTF8.GetBytes($responseHtml)
            $context.Response.ContentLength64 = $buffer.Length
            $context.Response.ContentType = "text/html"
            $context.Response.OutputStream.Write($buffer, 0, $buffer.Length)
            $context.Response.OutputStream.Close()

            if ($error_) {
                $errorDesc = $query["error_description"]
                throw "Authentication failed: $error_ - $errorDesc"
            }
            if ($returnedState -ne $state) {
                throw "Authentication failed: state mismatch (possible CSRF)."
            }
            if ([string]::IsNullOrWhiteSpace($code)) {
                throw "Authentication failed: no authorization code received."
            }
        }
        finally {
            $listener.Stop()
            $listener.Close()
        }

        # Exchange authorization code for access token
        $tokenUrl = "$authority/oauth2/v2.0/token"
        $tokenResponse = Invoke-RestMethod -Method Post -Uri $tokenUrl -Body @{
            grant_type    = "authorization_code"
            client_id     = $InteractiveClientId
            code          = $code
            redirect_uri  = $RedirectUri
            code_verifier = $codeVerifier
            scope         = $scope
        } -ContentType "application/x-www-form-urlencoded"

        return $tokenResponse.access_token
    }
}

# ── Web API Helpers ──────────────────────────────────────────────────────────

function Get-ApiHeaders {
    param([string]$accessToken)
    return @{
        "Authorization"    = "Bearer $accessToken"
        "OData-MaxVersion" = "4.0"
        "OData-Version"    = "4.0"
        "Accept"           = "application/json"
        "Content-Type"     = "application/json; charset=utf-8"
        "Prefer"           = "return=representation"
    }
}

function Invoke-DvGet {
    param([string]$baseUrl, [string]$query, [hashtable]$headers)

    $uri = "$baseUrl/api/data/$ApiVersion/$query"
    return Invoke-RestMethod -Method Get -Uri $uri -Headers $headers
}

function Invoke-DvPost {
    param([string]$baseUrl, [string]$entitySet, [object]$body, [hashtable]$headers)

    $uri = "$baseUrl/api/data/$ApiVersion/$entitySet"
    $json = $body | ConvertTo-Json -Depth 10
    return Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($json))
}

function Invoke-DvPatch {
    param([string]$baseUrl, [string]$entitySet, [string]$id, [object]$body, [hashtable]$headers)

    $uri = "$baseUrl/api/data/$ApiVersion/${entitySet}(${id})"
    $json = $body | ConvertTo-Json -Depth 10
    Invoke-RestMethod -Method Patch -Uri $uri -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($json)) | Out-Null
}

# ── Solution Component Helper ────────────────────────────────────────────────

function Add-SolutionComponent {
    param(
        [string]$baseUrl,
        [hashtable]$headers,
        [string]$solutionName,
        [string]$componentId,
        [int]$componentType
    )

    $body = @{
        ComponentId           = $componentId
        ComponentType         = $componentType
        SolutionUniqueName    = $solutionName
        AddRequiredComponents = $false
    }

    $uri = "$baseUrl/api/data/$ApiVersion/AddSolutionComponent"
    $json = $body | ConvertTo-Json -Depth 5
    Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($json)) | Out-Null
}

# ── Assembly Metadata ────────────────────────────────────────────────────────

function Get-AssemblyMetadata {
    param([string]$path)

    $resolvedPath = (Resolve-Path $path).Path

    if (-not (Test-Path $resolvedPath)) {
        throw "Assembly file not found: $resolvedPath"
    }

    # Load from bytes to avoid file locking
    $bytes = [System.IO.File]::ReadAllBytes($resolvedPath)
    $assembly = [System.Reflection.Assembly]::Load($bytes)
    $asmName = $assembly.GetName()

    $publicKeyToken = [System.BitConverter]::ToString($asmName.GetPublicKeyToken()).Replace("-", "").ToLower()

    return @{
        Name           = $asmName.Name
        Version        = $asmName.Version.ToString()
        PublicKeyToken = $publicKeyToken
        Content        = [System.Convert]::ToBase64String($bytes)
        FilePath       = $resolvedPath
        Assembly       = $assembly
    }
}

# ── Main ─────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Plugin Assembly Deployment" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# Validate inputs
if ([string]::IsNullOrWhiteSpace($CrmUrl)) {
    throw "CrmUrl is required. Provide via -CrmUrl parameter or CRM_URL environment variable."
}

# Normalize URL
$CrmUrl = $CrmUrl.TrimEnd('/')
if (-not $CrmUrl.StartsWith("https://")) {
    $CrmUrl = "https://$CrmUrl"
}

Write-Host "Environment: $CrmUrl"
Write-Host ""

# Extract assembly metadata
Write-Host "Loading assembly metadata..."
$metadata = Get-AssemblyMetadata -path $AssemblyPath

Write-Host "  Name:            $($metadata.Name)"
Write-Host "  Version:         $($metadata.Version)"
Write-Host "  PublicKeyToken:  $($metadata.PublicKeyToken)"
Write-Host "  File:            $($metadata.FilePath)"
Write-Host ""

# Authenticate
if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
    Write-Host "Using pre-obtained access token (federated/OIDC auth)."
} else {
    $AccessToken = Get-AccessToken -crmUrl $CrmUrl -username $Username -clientId $ClientId -clientSecret $ClientSecret -tenantId $TenantId
}
Write-Host "Authenticated successfully!" -ForegroundColor Green
Write-Host ""

# Set up headers
$headers = Get-ApiHeaders -accessToken $AccessToken

# Verify connection
Write-Host "Verifying connection..."
$whoAmI = Invoke-RestMethod -Method Get -Uri "$CrmUrl/api/data/$ApiVersion/WhoAmI" -Headers $headers
Write-Host "Connected as user: $($whoAmI.UserId)"
Write-Host ""

# Check if assembly already exists
Write-Host "Checking for existing assembly registration..."
$query = "pluginassemblies?`$filter=name eq '$($metadata.Name)'&`$select=pluginassemblyid,version&`$top=1"
$result = Invoke-DvGet -baseUrl $CrmUrl -query $query -headers $headers

if (@($result.value).Count -gt 0) {
    $existingId = $result.value[0].pluginassemblyid
    $existingVersion = $result.value[0].version

    Write-Host "  Found existing assembly (ID: $existingId)" -ForegroundColor Yellow
    Write-Host "  Current version: $existingVersion"
    Write-Host "  New version:     $($metadata.Version)"
    Write-Host ""

    $updateBody = [ordered]@{
        content = $metadata.Content
        version = $metadata.Version
    }

    if ($PSCmdlet.ShouldProcess("$($metadata.Name) v$($metadata.Version)", "Update assembly")) {
        Invoke-DvPatch -baseUrl $CrmUrl -entitySet "pluginassemblies" -id $existingId -body $updateBody -headers $headers
        Write-Host "UPDATED assembly: $($metadata.Name) v$($metadata.Version)" -ForegroundColor Green
    }
    $assemblyId = $existingId
}
else {
    Write-Host "  No existing assembly found. Will create new registration." -ForegroundColor Yellow
    Write-Host ""

    $createBody = [ordered]@{
        name           = $metadata.Name
        version        = $metadata.Version
        publickeytoken = $metadata.PublicKeyToken
        culture        = "neutral"
        sourcetype     = 0  # Database
        isolationmode  = 2  # Sandbox
        content        = $metadata.Content
    }

    if ($PSCmdlet.ShouldProcess("$($metadata.Name) v$($metadata.Version)", "Create assembly")) {
        $created = Invoke-DvPost -baseUrl $CrmUrl -entitySet "pluginassemblies" -body $createBody -headers $headers
        $assemblyId = $created.pluginassemblyid
        Write-Host "CREATED assembly: $($metadata.Name) v$($metadata.Version) (ID: $assemblyId)" -ForegroundColor Green
    }
}

# Add assembly to solution
if (-not [string]::IsNullOrWhiteSpace($SolutionName) -and $assemblyId) {
    if ($PSCmdlet.ShouldProcess("$($metadata.Name) -> solution '$SolutionName'", "Add to solution")) {
        Write-Host ""
        Write-Host "Adding assembly to solution '$SolutionName'..."
        Add-SolutionComponent -baseUrl $CrmUrl -headers $headers -solutionName $SolutionName -componentId $assemblyId -componentType 91
        Write-Host "Added assembly to solution '$SolutionName'" -ForegroundColor Green
    }
}

# ── Register plugin types ─────────────────────────────────────────────────────

if ($assemblyId) {
    Write-Host ""
    Write-Host "Discovering plugin types..."

    $discoveredTypeNames = @()
    try {
        $discoveredTypeNames = $metadata.Assembly.GetTypes() | Where-Object {
            $_.IsClass -and $_.IsPublic -and (-not $_.IsAbstract) -and
            (($_.GetInterfaces() | ForEach-Object FullName) -contains 'Microsoft.Xrm.Sdk.IPlugin')
        } | ForEach-Object FullName
    }
    catch [System.Reflection.ReflectionTypeLoadException] {
        $discoveredTypeNames = $_.Exception.Types | Where-Object { $_ -ne $null } | Where-Object {
            $_.IsClass -and $_.IsPublic -and (-not $_.IsAbstract) -and
            (($_.GetInterfaces() | ForEach-Object FullName) -contains 'Microsoft.Xrm.Sdk.IPlugin')
        } | ForEach-Object FullName
    }

    if (@($discoveredTypeNames).Count -eq 0) {
        Write-Host "  No IPlugin types discovered in assembly." -ForegroundColor Yellow
    }
    else {
        Write-Host "  Discovered $(@($discoveredTypeNames).Count) plugin type(s)."
        Write-Host ""

        foreach ($typeName in $discoveredTypeNames) {
            $query = "plugintypes?`$filter=typename eq '$typeName' and _pluginassemblyid_value eq $assemblyId&`$select=plugintypeid&`$top=1"
            $existing = Invoke-DvGet -baseUrl $CrmUrl -query $query -headers $headers

            if (@($existing.value).Count -gt 0) {
                Write-Host "  EXISTS: $typeName" -ForegroundColor Yellow
            }
            else {
                $typeBody = [ordered]@{
                    name                          = $typeName
                    typename                      = $typeName
                    friendlyname                  = $typeName
                    description                   = ""
                    "pluginassemblyid@odata.bind" = "/pluginassemblies($assemblyId)"
                }

                if ($PSCmdlet.ShouldProcess($typeName, "Create plugin type")) {
                    $created = Invoke-DvPost -baseUrl $CrmUrl -entitySet "plugintypes" -body $typeBody -headers $headers
                    Write-Host "  CREATED: $typeName (ID: $($created.plugintypeid))" -ForegroundColor Green
                }
            }
        }
    }
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Done." -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""
