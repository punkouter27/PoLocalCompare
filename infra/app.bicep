targetScope = 'resourceGroup'

// PoLocalCompare: the app's own resources — storage, the F1 plan and the App Service.

param location string

@description('Globally unique App Service name (also the *.azurewebsites.net host)')
param appServiceName string

@description('Globally unique storage account name')
param storageAccountName string

@description('Shared Key Vault URI (PoShared)')
param keyVaultUri string

@description('Shared App Insights connection string (PoShared)')
param appInsightsConnectionString string

@description('GitHub Actions service principal object id. Empty = no grant.')
param ciPrincipalId string

// The app creates its tables at runtime (StorageTables.EnsureAllAsync) and connects with its
// system-assigned managed identity (standards §5.4). Nothing uses an account key, so shared-key
// auth is off.
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
  }
}

// Windows Free (F1).
resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'asp-PoLocalCompare-f1'
  location: location
  sku: {
    name: 'F1'
    tier: 'Free'
  }
  properties: {
    reserved: false
  }
}

// ─── App Service (Windows, .NET 10) ────────────────────────────────────────────────
resource appService 'Microsoft.Web/sites@2023-12-01' = {
  name: appServiceName
  location: location
  kind: 'app'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      // F1 (Free) does not support AlwaysOn.
      alwaysOn: false
      netFrameworkVersion: 'v10.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      metadata: [
        {
          name: 'CURRENT_STACK'
          value: 'dotnet'
        }
      ]
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          // CI ships a built publish output; skip Oryx/Kudu build on deploy.
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'false'
        }
        {
          name: 'KeyVault__Uri'
          value: keyVaultUri
        }
        {
          // Identity-based storage access: the app resolves Table/Blob endpoints from the
          // account name via DefaultAzureCredential instead of a shared-key connection string.
          name: 'AzureStorage__AccountName'
          value: storageAccount.name
        }
        {
          // Feeds the single Serilog App Insights sink (Program.cs). Double underscore is the
          // App Service spelling of the ApplicationInsights:ConnectionString config key.
          name: 'ApplicationInsights__ConnectionString'
          value: appInsightsConnectionString
        }
      ]
    }
  }
}

// ─── RBAC ──────────────────────────────────────────────────────────────────────────
var storageTableDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
)
var storageBlobDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)
var websiteContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'de139f84-1756-47ae-9be6-808fbbe84772'
)

resource tableRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, appService.id, storageTableDataContributor)
  scope: storageAccount
  properties: {
    roleDefinitionId: storageTableDataContributor
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource blobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, appService.id, storageBlobDataContributor)
  scope: storageAccount
  properties: {
    roleDefinitionId: storageBlobDataContributor
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// CI deploys code only: Website Contributor on this RG is enough for `az webapp show` and the
// zip deploy, and deliberately not enough to run main.bicep (which writes role assignments).
resource ciWebsiteContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(ciPrincipalId)) {
  name: guid(resourceGroup().id, ciPrincipalId, websiteContributor)
  properties: {
    roleDefinitionId: websiteContributor
    principalId: ciPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output appServiceName string = appService.name
output appServiceUrl string = 'https://${appService.properties.defaultHostName}'
output storageAccountName string = storageAccount.name
output principalId string = appService.identity.principalId
