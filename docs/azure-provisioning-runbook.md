# Azure Provisioning Runbook — Dev environment

End-to-end commands to stand up the **Dev** environment for `adaptive-observability`. Executes Phase 2.1 (Dev vault), 2.3 (hosting + managed identity), and 2.4 (database + secret cutover) from [`DEVELOPMENT_PLAN.md`](../DEVELOPMENT_PLAN.md).

Run as the **subscription Owner / Contributor + User Access Administrator**. The first three sections (KV, MI, App Service) are billable; the SQL section reuses an existing server.

UAT and Prod follow the same shape — repeat with the env-specific names below once Dev is verified.

## Dev quick-start — Arlo, shared `AdaptiveToolsKeyVault`

Tailored values for the current Dev provisioning pass. RG-Contributor + Secrets Officer on the shared vault — you can run sections 2, 3, 4 yourself; you'll need one role-assignment line from someone with vault Owner / User Access Administrator (see §1 grant block).

```powershell
$Subscription = "Adaptive Subscription"
$Tenant       = "36e913b6-b1df-4592-a4f8-6f8ea39f7e20"
$Location     = "centralus"
$Rg           = "AdaptiveTools"
$Env          = "dev"
$KvName       = "AdaptiveToolsKeyVault"          # using the shared vault for Dev
$MiName       = "id-observability-dev"
$PlanName     = "asp-adaptive-shared"
$PlanSku      = "B1"
$AppName      = "obs-api-dev"
$SqlServer    = "adaptivetoolssql"
$DbName       = "ObservabilityDev"
$DbMaxSizeGb  = 32

az account set --subscription "$Subscription"
```

**Section map for this run:**
- **§1 Key Vault** — skip the `az keyvault create` and the four `secret set` lines (already in place from 2026-04-30 seed). The `Key Vault Administrator` self-grant is optional (you already have Secrets Officer + User on this vault).
- **§2 User-Assigned MI** — run as-is.
- **§2 grant MI Key Vault Secrets User** — **needs vault Owner / User Access Administrator** to run. After §2 finishes, capture the MI `principalId` and the vault resourceId; the role-assignment command is one line — send it to whoever has the perm.
- **§3 App Service Plan + App Service** — run as-is.
- **§4 SQL** — run §4a (firewall), §4b (DB), §4c (T-SQL — promote yourself to AAD admin on `$SqlServer` first via `az sql server ad-admin create --display-name "Arlo" --object-id (az ad signed-in-user show --query id -o tsv) --server $SqlServer -g $Rg`), §4d (real connection string into KV).
- **§5 Deploy + smoke** — run as-is.

## Inputs (generic reference, all envs)

Edit these once, then paste each section. The block above is the pre-filled Dev variant.

```powershell
$Subscription   = "Adaptive Subscription"        # az account show
$Tenant         = "<your-tenant-guid>"           # az account show --query tenantId -o tsv
$Location       = "centralus"
$Rg             = "AdaptiveTools"                # existing
$Env            = "dev"                          # dev | uat | prod
$KvName         = "kv-observability-$Env"        # 3-24 chars, must be globally unique
$MiName         = "id-observability-$Env"
$PlanName       = "asp-adaptive-shared"          # one shared plan (Brandon's decision)
$PlanSku        = "B1"                           # right-size after measuring; B1 fits Dev
$AppName        = "obs-api-$Env"                 # globally unique, becomes <name>.azurewebsites.net
$SqlServer      = "adaptivetoolssql"             # existing
$DbName         = "Observability$($Env.Substring(0,1).ToUpper() + $Env.Substring(1))" # ObservabilityDev
$DbSku          = "GP_S_Gen5_1"                  # serverless, autopause; cheapest sensible default
$DbMaxSizeGb    = 32

az account set --subscription "$Subscription"
```

## 1. Key Vault (Phase 2.1 — Dev portion)

Fresh dedicated KV — replaces the shared `AdaptiveToolsKeyVault` previously used for Dev.

```powershell
az keyvault create `
    --name $KvName `
    --resource-group $Rg `
    --location $Location `
    --enable-rbac-authorization true `
    --retention-days 90 `
    --enable-purge-protection $($Env -eq "prod")
```

Seed the four required secrets ([`KeyVaultConfiguration.cs`](../backend/src/Observability.Api/Configuration/KeyVaultConfiguration.cs)) with placeholders so the API can start before the real values are in. Real `ObservabilityDbConnection` lands in section 4.

```powershell
az keyvault secret set --vault-name $KvName --name "ObservabilityDbConnection" --value "placeholder"
az keyvault secret set --vault-name $KvName --name "ApiKeyHashPepper"          --value (New-Guid).Guid
az keyvault secret set --vault-name $KvName --name "JwtSigningKey"             --value (New-Guid).Guid
az keyvault secret set --vault-name $KvName --name "EncryptionKey"             --value (New-Guid).Guid
```

Grant yourself `Key Vault Administrator` so you can read/manage secrets manually during setup:

```powershell
$Me = az ad signed-in-user show --query id -o tsv
az role assignment create `
    --assignee $Me `
    --role "Key Vault Administrator" `
    --scope (az keyvault show --name $KvName --query id -o tsv)
```

## 2. User-assigned Managed Identity (Phase 2.3 — identity)

```powershell
az identity create `
    --name $MiName `
    --resource-group $Rg `
    --location $Location

$MiId       = az identity show --name $MiName --resource-group $Rg --query id          -o tsv
$MiClientId = az identity show --name $MiName --resource-group $Rg --query clientId    -o tsv
$MiPrinc    = az identity show --name $MiName --resource-group $Rg --query principalId -o tsv

# Grant the MI Key Vault Secrets User on this vault only.
az role assignment create `
    --assignee-object-id $MiPrinc `
    --assignee-principal-type ServicePrincipal `
    --role "Key Vault Secrets User" `
    --scope (az keyvault show --name $KvName --query id -o tsv)
```

## 3. App Service plan + instance (Phase 2.3 — hosting)

```powershell
# Plan: reuse one across envs. Skip this command on UAT/Prod runs.
az appservice plan create `
    --name $PlanName `
    --resource-group $Rg `
    --location $Location `
    --is-linux `
    --sku $PlanSku

# Per-env App Service instance.
az webapp create `
    --name $AppName `
    --plan $PlanName `
    --resource-group $Rg `
    --runtime "DOTNETCORE:8.0"

# Attach the user-assigned MI.
az webapp identity assign `
    --name $AppName `
    --resource-group $Rg `
    --identities $MiId

# Configuration. KeyVault__Uri is what KeyVaultConfiguration.cs reads.
az webapp config appsettings set `
    --name $AppName `
    --resource-group $Rg `
    --settings `
        "KeyVault__Uri=https://$KvName.vault.azure.net/" `
        "ASPNETCORE_ENVIRONMENT=Development" `
        "AZURE_CLIENT_ID=$MiClientId"

# AZURE_CLIENT_ID tells DefaultAzureCredential which user-assigned MI to use.
# Without it, the SDK either picks the system-assigned MI (we don't have one) or
# fails ambiguously when multiple identities are attached.

# Capture the outbound IP set — needed for the SQL firewall rule in section 4.
az webapp show --name $AppName --resource-group $Rg `
    --query possibleOutboundIpAddresses -o tsv
```

Save the outbound IP list — section 4 needs it.

## 4. Azure SQL — database, network access, MI grant (Phase 2.4)

### 4a. Re-enable public network access on the existing server

> **Posture note:** this reverses a prior explicit hardening. App Service VNet integration + private endpoint is the cleaner long-term answer; Brandon's call was to start with public + firewall for Dev and revisit at UAT/Prod. Re-sync the firewall after any App Service plan scale-out (outbound IPs change).

```powershell
az sql server update `
    --name $SqlServer `
    --resource-group $Rg `
    --enable-public-network true

# Firewall rule per outbound IP. Replace the list with whatever section 3 printed.
$AppOutboundIps = @("203.0.113.10","203.0.113.11","203.0.113.12")  # paste your real list
foreach ($ip in $AppOutboundIps) {
    az sql server firewall-rule create `
        --resource-group $Rg `
        --server $SqlServer `
        --name "obs-$Env-$($ip.Replace('.','-'))" `
        --start-ip-address $ip `
        --end-ip-address $ip
}
```

### 4b. Create the per-env database

```powershell
az sql db create `
    --resource-group $Rg `
    --server $SqlServer `
    --name $DbName `
    --edition GeneralPurpose `
    --family Gen5 `
    --capacity 1 `
    --compute-model Serverless `
    --auto-pause-delay 60 `
    --max-size $($DbMaxSizeGb)GB
```

### 4c. Grant the MI access to the new DB

The SQL server has `azureAdOnlyAuthentication=true` — SQL auth is disabled, so this MUST be done via T-SQL as the AAD admin. Connect to `$SqlServer` ↦ `$DbName` in SSMS / Azure Data Studio (or `sqlcmd -G`) as `brandon@adaptivesoftwarellc.com` (or whichever account holds AAD-admin on the server now that Arlo is owner), then run:

```sql
CREATE USER [obs-api-dev] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [obs-api-dev];
ALTER ROLE db_datawriter ADD MEMBER [obs-api-dev];
ALTER ROLE db_ddladmin  ADD MEMBER [obs-api-dev];  -- needed because MigrateAsync runs at startup
```

Use the App Service name (`$AppName`) as the principal name — Azure AD resolves it to the attached user-assigned MI when the App Service connects. If you used a different `$MiName`, substitute that here instead.

### 4d. Put the real connection string in Key Vault

```powershell
$ConnString = "Server=tcp:$SqlServer.database.windows.net,1433;Initial Catalog=$DbName;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

az keyvault secret set `
    --vault-name $KvName `
    --name "ObservabilityDbConnection" `
    --value $ConnString
```

`Authentication=Active Directory Default` makes the SqlClient ask `DefaultAzureCredential` for a token. In the App Service that resolves to the user-assigned MI we attached in section 3.

## 5. Deploy + smoke test

Deploy via GitHub Actions (`.github/workflows/backend.yml` does build + test today — add a deploy step under the same workflow on a separate PR, or push a zip manually for the first smoke):

```powershell
# Manual one-shot deploy for the first smoke. Use CI for normal deploys.
dotnet publish backend/src/Observability.Api/Observability.Api.csproj -c Release -o publish/
Compress-Archive -Path publish/* -DestinationPath publish.zip -Force
az webapp deploy --resource-group $Rg --name $AppName --src-path publish.zip --type zip
```

Smoke checks:

```powershell
# 1. Health
curl "https://$AppName.azurewebsites.net/health"
# Expect: 200, body "Healthy"

# 2. MigrateAsync ran cleanly — Sessions/Events/Errors/etc. should exist in $DbName.
#    Confirm via Azure Data Studio query: SELECT name FROM sys.tables;

# 3. An ingestion smoke event using the dev API key (provision one via the dashboard
#    /admin/apps page once the deploy is up, or seed one manually).
```

If `/health` returns 500 with a `KeyVault` exception in App Service log stream:
- Check `az webapp log tail --name $AppName --resource-group $Rg`.
- Most common cause: the MI doesn't have `Key Vault Secrets User` on the right vault (re-run section 2's role assignment).
- Second most common: `AZURE_CLIENT_ID` not set or wrong (section 3).

## Cost ballpark (Dev)

| Resource | SKU | Idle cost |
|---|---|---|
| Key Vault | standard | ~$0.03/secret/month |
| User-assigned MI | n/a | free |
| App Service Plan | B1 | ~$13/month |
| App Service instance | shares plan | $0 (cost is on the plan) |
| Azure SQL DB | GP_S_Gen5_1 serverless | ~$5–15/month (autopauses after 60 min idle) |
| Public IPs / firewall rules | n/a | free |

UAT and Prod cost more in proportion (P1V3 plan for prod is the usual jump). Confirm the SKU before running the section 3 plan create on UAT/Prod.

## What this runbook does NOT cover

- **CI/CD deploy step.** This runbook deploys manually for the first smoke. Wiring publish-to-App-Service into `.github/workflows/backend.yml` (with OIDC federation against the MI so no service principal secret lives in GitHub) is a follow-up.
- **UAT and Prod KVs (Phase 2.1 remaining).** Re-run sections 1–4 with `$Env="uat"` then `$Env="prod"`. Prod must set `--enable-purge-protection true` on the KV (already conditional above) and revisit the public-network firewall decision.
- **Diagnostic logs + alerts (Phase 8.7 cross-cut).** Set up Log Analytics workspace + KV diagnostic settings as part of Phase 8.
- **Key rotation runbooks.** Already documented in [`docs/azure-key-vault-setup.md`](azure-key-vault-setup.md).
