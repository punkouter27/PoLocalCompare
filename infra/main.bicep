targetScope = 'subscription'

// Provisions PoLocalCompare from nothing into one subscription (since 2026-10-25 the
// punkouter27@gmail.com one). Run through SCRIPTS/provision-azure.ps1, which also seeds the vault
// and wires GitHub OIDC — CI only deploys code onto what this creates.

@description('Azure region for both resource groups')
param location string = 'westus2'

@description('Object id of whoever runs the bootstrap; granted Key Vault Secrets Officer so it can seed the vault. Empty = no grant.')
param deployerObjectId string = ''

@description('Service principal object id of the GitHub Actions identity; granted Website Contributor on the app RG. Empty = no grant.')
param ciPrincipalId string = ''

// Web app host, storage and Key Vault names are global. The original outlook-account deployment
// still holds app-polocalcompare-win2 / polocalcomparedevsa2 / kv-poshared2, so every global name here
// carries a suffix that is stable per subscription.
var suffix = take(uniqueString(subscription().id), 6)

resource sharedRg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'PoShared'
  location: location
}

resource appRg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'PoLocalCompare'
  location: location
}

module shared 'shared.bicep' = {
  name: 'poshared'
  scope: sharedRg
  params: {
    location: location
    keyVaultName: 'kv-poshared-${suffix}'
    deployerObjectId: deployerObjectId
  }
}

module app 'app.bicep' = {
  name: 'polocalcompare-app'
  scope: appRg
  params: {
    location: location
    appServiceName: 'app-polocalcompare-${suffix}'
    storageAccountName: 'polocalcompare${suffix}'
    keyVaultUri: shared.outputs.keyVaultUri
    appInsightsConnectionString: shared.outputs.appInsightsConnectionString
    ciPrincipalId: ciPrincipalId
  }
}

module keyVaultAccess 'keyvault-access.bicep' = {
  name: 'polocalcompare-kv-access'
  scope: sharedRg
  params: {
    principalId: app.outputs.principalId
    keyVaultName: shared.outputs.keyVaultName
  }
}

output appServiceName string = app.outputs.appServiceName
output appServiceUrl string = app.outputs.appServiceUrl
output storageAccountName string = app.outputs.storageAccountName
output keyVaultName string = shared.outputs.keyVaultName
output keyVaultUri string = shared.outputs.keyVaultUri
