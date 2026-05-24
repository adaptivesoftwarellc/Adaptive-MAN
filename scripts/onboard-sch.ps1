<#
.SYNOPSIS
    Phase 6.6 -- onboard SCH_UI + SCH_API in adaptive-observability.

.DESCRIPTION
    Idempotent. Creates the two SCH application rows + Dev/Prod environments,
    then mints one PublicClient key (for SCH_UI) and one ServerApi key
    (for SCH_API) per environment. Plaintext key values are printed once --
    capture them and store in SCH's secrets immediately. They are NOT
    retrievable from the platform after this script exits.

.PARAMETER ApiBase
    Adaptive Observability API base URL. Defaults to obs-api-dev.

.PARAMETER AdminKey
    The X-Observability-Admin-Key value. If omitted, pulls from the
    AdaptiveToolsKeyVault secret named ObservabilityAdminKey via az CLI.

.EXAMPLE
    .\onboard-sch.ps1
    # Pulls admin key from KV, hits obs-api-dev, mints 4 keys (sch-ui Dev/Prod, sch-api Dev/Prod).
#>

param(
    [string]$ApiBase = "https://obs-api-dev.azurewebsites.net",
    [string]$AdminKey = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($AdminKey)) {
    Write-Host "Fetching ObservabilityAdminKey from AdaptiveToolsKeyVault..."
    $AdminKey = az keyvault secret show `
        --vault-name "AdaptiveToolsKeyVault" `
        --name "ObservabilityAdminKey" `
        --query value -o tsv
    if ([string]::IsNullOrWhiteSpace($AdminKey)) {
        throw "Could not fetch admin key. Pass -AdminKey or check az login."
    }
}

$headers = @{
    "X-Observability-Admin-Key" = $AdminKey
    "Content-Type"              = "application/json"
}

function Create-App([string]$slug, [string]$name, [string]$description) {
    $body = @{
        name         = $name
        slug         = $slug
        description  = $description
        environments = @("Development", "Production")
    } | ConvertTo-Json -Compress

    Write-Host ""
    Write-Host "POST /api/admin/apps  ($slug)" -ForegroundColor Cyan
    $resp = Invoke-RestMethod -Method Post `
        -Uri "$ApiBase/api/admin/apps" `
        -Headers $headers `
        -Body $body
    Write-Host ("  -> created={0} app_id={1} envs={2}" -f $resp.created, $resp.application_id, ($resp.environments -join ","))
    return $resp
}

function Mint-Key([string]$slug, [string]$env, [string]$keyType) {
    $body = @{ key_type = $keyType } | ConvertTo-Json -Compress

    Write-Host ""
    Write-Host "POST /api/admin/apps/$slug/environments/$env/keys  ($keyType)" -ForegroundColor Cyan
    $resp = Invoke-RestMethod -Method Post `
        -Uri "$ApiBase/api/admin/apps/$slug/environments/$env/keys" `
        -Headers $headers `
        -Body $body
    Write-Host "  PLAINTEXT KEY (shown once -- capture now):" -ForegroundColor Yellow
    Write-Host ("    {0}" -f $resp.key) -ForegroundColor Yellow
    Write-Host ("  prefix={0}  key_type={1}  env={2}" -f $resp.key_prefix, $resp.key_type, $env)
    return $resp
}

Write-Host "=== Adaptive Observability -- SCH onboarding (Phase 6.6) ==="
Write-Host "API base: $ApiBase"

Create-App -slug "sch-ui"  -name "SCH UI"  -description "Strategic Health Care -- frontend (React + Vite + MSAL)" | Out-Null
Create-App -slug "sch-api" -name "SCH API" -description "Strategic Health Care -- backend (.NET 10, ASP.NET Core)"  | Out-Null

Write-Host ""
Write-Host "--- Minting public-client keys for SCH_UI ---"
Mint-Key -slug "sch-ui" -env "Development" -keyType "PublicClient" | Out-Null
Mint-Key -slug "sch-ui" -env "Production"  -keyType "PublicClient" | Out-Null

Write-Host ""
Write-Host "--- Minting server keys for SCH_API ---"
Mint-Key -slug "sch-api" -env "Development" -keyType "ServerApi" | Out-Null
Mint-Key -slug "sch-api" -env "Production"  -keyType "ServerApi" | Out-Null

Write-Host ""
Write-Host "=== DONE ===" -ForegroundColor Green
Write-Host "Next steps:"
Write-Host "  1. Store the four plaintext keys above in the appropriate secret stores:"
Write-Host "     * SCH_UI:  add VITE_OBSERVABILITY_KEY (Dev + Prod) as GitHub repo secrets"
Write-Host "                VITE_OBSERVABILITY_URL = $ApiBase"
Write-Host "     * SCH_API: add AdaptiveObservability--ApiKey (Dev + Prod) to SCH's Key Vault"
Write-Host "                set AdaptiveObservability:Enabled=true and HostUrl=$ApiBase"
Write-Host "  2. Merge the two SCH feature/adaptive-observability PRs."
Write-Host "  3. Begin the 5-business-day SCH Dev shakedown soak."
