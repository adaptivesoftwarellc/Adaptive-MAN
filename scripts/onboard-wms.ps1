<#
.SYNOPSIS
    Phase 7 -- onboard WMSSite + WMSAPI in adaptive-observability.

.DESCRIPTION
    Idempotent. Creates the two WMS application rows + Dev/Prod environments,
    then mints one PublicClient key (for WMSSite) and one ServerApi key
    (for WMSAPI) per environment. Plaintext key values are printed once --
    capture them and store in the WMS secret stores immediately. They are NOT
    retrievable from the platform after this script exits.

    Note: WMSSite lives on the `adaptivesoftwarellc` org and WMSAPI on
    `bdadaptivewoundmsllc` -- same product, two orgs. The two keys land in
    different secret stores (see "Next steps" below). See docs/audits/wmssite.md
    and docs/audits/wmsapi.md.

.PARAMETER ApiBase
    Adaptive Observability API base URL. Defaults to obs-api-dev.

.PARAMETER AdminKey
    The X-Observability-Admin-Key value. If omitted, pulls from the
    AdaptiveToolsKeyVault secret named ObservabilityAdminKey via az CLI.

.EXAMPLE
    .\onboard-wms.ps1
    # Pulls admin key from KV, hits obs-api-dev, mints 4 keys (wms-site Dev/Prod, wms-api Dev/Prod).
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
    $envNames = ($resp.environments | ForEach-Object { $_.name }) -join ","
    Write-Host ("  -> created={0} app_id={1} envs={2}" -f $resp.created, $resp.id, $envNames)
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
    Write-Host ("    {0}" -f $resp.plaintext_key) -ForegroundColor Yellow
    Write-Host ("  key_id={0}  key_type={1}  env={2}" -f $resp.id, $resp.key_type, $env)
    return $resp
}

Write-Host "=== Adaptive Observability -- WMS onboarding (Phase 7) ==="
Write-Host "API base: $ApiBase"

Create-App -slug "wms-site" -name "WMS Site" -description "Wound Management -- frontend (React + Vite + MSAL)"        | Out-Null
Create-App -slug "wms-api"  -name "WMS API"  -description "Wound Management -- backend (.NET 8, ASP.NET Core, JWT)"   | Out-Null

Write-Host ""
Write-Host "--- Minting public-client keys for WMSSite ---"
Mint-Key -slug "wms-site" -env "Development" -keyType "PublicClient" | Out-Null
Mint-Key -slug "wms-site" -env "Production"  -keyType "PublicClient" | Out-Null

Write-Host ""
Write-Host "--- Minting server keys for WMSAPI ---"
Mint-Key -slug "wms-api" -env "Development" -keyType "ServerApi" | Out-Null
Mint-Key -slug "wms-api" -env "Production"  -keyType "ServerApi" | Out-Null

Write-Host ""
Write-Host "=== DONE ===" -ForegroundColor Green
Write-Host "Next steps:"
Write-Host "  1. Store the four plaintext keys above in the appropriate secret stores:"
Write-Host "     * WMSSite (adaptivesoftwarellc/WMSSite):"
Write-Host "                add VITE_OBSERVABILITY_KEY (Dev + Prod) as GitHub repo secrets"
Write-Host "                VITE_OBSERVABILITY_URL = $ApiBase ; VITE_OBSERVABILITY_ENABLED = true"
Write-Host "     * WMSAPI (bdadaptivewoundmsllc/WMSAPI):"
Write-Host "                add AdaptiveObservability:ApiKey (Dev + Prod) to WMS's Key Vault / env"
Write-Host "                set AdaptiveObservability:Enabled=true and HostUrl=$ApiBase"
Write-Host "  2. Merge the WMSSite + WMSAPI feature/adaptive-observability PRs (Issues 7.1 / 7.2)."
Write-Host "  3. Watch obs-api-dev for the first wms-site / wms-api events + SafetyViolations."
