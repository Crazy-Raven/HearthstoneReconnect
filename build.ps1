param(
    [string]$IconPath = (Join-Path $PSScriptRoot 'HearthstoneReconnect.ico'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'build\HearthstoneReconnect.exe')
)

$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compilerPath)) {
    throw 'The Windows .NET Framework x64 compiler was not found.'
}
if (!(Test-Path -LiteralPath $IconPath)) {
    throw 'The icon was not found. Specify HearthstoneReconnect.ico with -IconPath.'
}
$IconPath = (Get-Item -LiteralPath $IconPath).FullName
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($OutputPath)
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$arguments = @(
    '/nologo', '/target:winexe', '/platform:x64', '/codepage:65001', '/optimize+',
    ('/out:' + $OutputPath),
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),
    ('/win32icon:' + $IconPath),
    ('/resource:' + (Join-Path $PSScriptRoot 'LICENSE') + ',HearthstoneReconnect.LICENSE.txt'),
    '/reference:System.dll', '/reference:System.Core.dll',
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    (Join-Path $PSScriptRoot 'HsReconnect.cs'),
    (Join-Path $PSScriptRoot 'AssemblyInfo.cs')
)
& $compilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw ('Compilation failed with exit code ' + $LASTEXITCODE) }
Get-Item -LiteralPath $OutputPath | Select-Object FullName, Length
