using 'main.bicep'

// Defaults for a provisioning run. scripts/provision-azure.ps1 deploys main.bicep with
// deployerObjectId and ciPrincipalId filled in for the signed-in owner and the GitHub identity.
param location = 'westus2'
param deployerObjectId = ''
param ciPrincipalId = ''
