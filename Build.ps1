[String]$ScriptDir = Split-Path $Script:MyInvocation.MyCommand.Path
$ErrorActionPreference = "Stop"

Import-Module "$ScriptDir\build.vs\GitVersion.psm1"
Import-Module "$ScriptDir\build.vs\Clean.psm1"
Import-Module "$ScriptDir\build.vs\RestoreNuGet.psm1"
Import-Module "$ScriptDir\build.vs\PreBuildWizard.psm1"
Import-Module "$ScriptDir\build.vs\Build.psm1"
Import-Module "$ScriptDir\build.vs\LicenseCollector.psm1"

# -------------------------------------------------------------------------------------------------------------------------------------

# #################################
# configuration
# #################################

[String]  $ProductName          = "LicenseCollector"
[String]  $SolutionPath         = "$ScriptDir\LicenseCollector.sln"
[String]  $LicenseTemplatePath  = "$ScriptDir\THIRD_PARTY_NOTICES.template"

$MsbuildConfigurations = @('Release')
$MsbuildPlatforms = @('Any CPU')

# -------------------------------------------------------------------------------------------------------------------------------------

# remove old build output
Clean `
	-OutputPaths @("$ScriptDir\_build", "$ScriptDir\_deploy") `
	-PauseOnError

# let GitVersion determine the version number from the git history via tags
GitVersion -PauseOnError

# restore NuGet packages
RestoreNuGet `
	-SolutionPath "$SolutionPath" `
	-PauseOnError

# patch templates and assembly infos with current version number
# check consistency of NuGet packages
PreBuildWizard `
	-SolutionPath "$SolutionPath" `
	-PauseOnError

# build projects
Build `
	-SolutionPath "$SolutionPath" `
	-MsbuildConfigurations $MsbuildConfigurations `
	-MsbuildPlatforms $MsbuildPlatforms `
	-SkipConsistencyCheck `
	-PauseOnError

# collect license for release build
dotnet "_build\.out\LicenseCollector\AnyCPU.Release\net6.0\LicenseCollector.dll" `
  --SolutionFilePath "$SolutionPath" `
  --Configuration "Release" `
  --Platform "Any CPU" `
  --LicenseTemplatePath "$LicenseTemplatePath" `
  "$ScriptDir\THIRD_PARTY_NOTICES"

Read-Host "Press ANY key..."
