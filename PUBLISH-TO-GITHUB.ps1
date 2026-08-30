param(
    [string]$RepositoryUrl = 'https://github.com/K2daD/ArcadeBridge.git'
)

$ErrorActionPreference = 'Stop'
$repositoryFolder = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'Git is not installed. Install GitHub Desktop or Git for Windows, then run this file again.'
}

Set-Location -LiteralPath $repositoryFolder

if (-not (Test-Path -LiteralPath '.git')) {
    git init
}

git add .
git commit -m 'ArcadeBridge 1.0.0'
git branch -M main

$existingRemote = git remote get-url origin 2>$null
if ($LASTEXITCODE -ne 0) {
    git remote add origin $RepositoryUrl
}
elseif ($existingRemote -ne $RepositoryUrl) {
    git remote set-url origin $RepositoryUrl
}

git push -u origin main

if ($LASTEXITCODE -ne 0) {
    throw 'GitHub did not accept the upload. Sign in with GitHub Desktop or Git Credential Manager, then try again.'
}

Write-Host 'ArcadeBridge was uploaded successfully.' -ForegroundColor Green
