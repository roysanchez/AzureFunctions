#Requires -Module Az.Websites
param (
    [Parameter(Mandatory=$True)]
    [string]
    $subscriptionId,
    [Parameter(Mandatory=$True)]
    [string]
    $resourceGroupName,
    [Parameter(Mandatory=$true, HelpMessage="Nombre de la funcion en Azure")]
    [Alias("an")]
    [string]
    $appName,
    [Parameter(Mandatory=$true, HelpMessage="Ruta del zip de despliegue")]
    [Alias("fp", "path")]
    [string]
    $filePath,
    [string]
    $templateFilePath = "template.json",
    [string]
    $parametersFilePath = "parameters.json"
)

if(![System.IO.File]::Exists($filePath))
{
    throw [System.IO.FileNotFoundException] "Could not find file $path"
}

Write-Host "Logging in...";
Connect-AzAccount;

Write-Host "Selecting subscription '$subscriptionId'";
Select-AzSubscription -Subscription (Get-AzSubscription -SubscriptionId $subscriptionId)

#Create or check for existing resource group
$resourceGroup = Get-AzResourceGroup -Name $resourceGroupName -ErrorAction SilentlyContinue
if(!$resourceGroup)
{
    Write-Host "Resource group '$resourceGroupName' does not exist";
    if(!$resourceGroupLocation) {
        Write-Host "To create a new resource group, please enter a location."
        $resourceGroupLocation = Read-Host "resourceGroupLocation";
    }
    Write-Host "Creating resource group '$resourceGroupName' in location '$resourceGroupLocation'";
    New-AzResourceGroup -Name $resourceGroupName -Location $resourceGroupLocation
}
else{
    Write-Host "Using existing resource group '$resourceGroupName'";
}


$app = Get-AzWebApp -Name $appName -ErrorAction SilentlyContinue

if(!$app)
{
    Write-Host "App doesn't exists, starting deployment..."
    
    New-AzResourceGroupDeployment -Name $appName -ResourceGroupName $resourceGroupName -TemplateFile  $templateFilePath -TemplateParameterFile $parametersFilePath -SkipTemplateParameterPrompt
    $app = Get-AzWebApp -Name $appName
}

if($app)
{
    Write-Host "Publishing the app..."
    Publish-AzWebApp -WebApp $app -ArchivePath $filePath -Force:$true
}
else
{
    throw "Error creating app"
}