[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = $PSScriptRoot
$distPath = Join-Path $projectRoot 'dist'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Invoke-Dotnet {
    param([string[]]$Arguments)
    Write-Host ('> dotnet ' + ($Arguments -join ' '))
    & $script:dotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

function Get-InputDigest {
    $extensions = @('.cs','.xaml','.csproj','.manifest','.ps1','.ico','.png','.dll','.traineddata','.txt','.md')
    $rows = @(Get-ChildItem -LiteralPath $projectRoot -Recurse -File | Where-Object {
        $relative = $_.FullName.Substring($projectRoot.Length + 1)
        $parts = $relative.Split('\')
        $excluded = $parts[0] -in @('bin','obj','dist','.vs','.git','node_modules')
        $document = $parts.Count -eq 1 -and $_.Extension -eq '.md'
        -not $excluded -and -not $document -and $_.Extension.ToLowerInvariant() -in $extensions -and
            $relative -notmatch '(?i)(^|\\)(\.env[^\\]*|credentials?[^\\]*|secrets?[^\\]*|tokens?[^\\]*)(\\|$)'
    } | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($projectRoot.Length + 1).Replace('\','/') + "`t" + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    })
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes(($rows -join "`n")))).Replace('-','').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

try {
    Write-Host 'CratePilot - offline production build'
    Write-Host 'Compilation and packaging only. No application session, screen capture or input is executed.'
    $before = Get-InputDigest
    Write-Host ('Input SHA256 before: ' + $before)
    $script:dotnetPath = (Get-Command 'dotnet.exe' -ErrorAction Stop).Source
    $referencesPath = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
    if (-not (Test-Path -LiteralPath (Join-Path $referencesPath 'PresentationFramework.dll'))) {
        throw 'The .NET Framework 4.8 Developer Pack is required. This build does not install or download it.'
    }
    $required = @('PortableOcr\Data\eng.traineddata')
    foreach ($architecture in @('x64','x86')) {
        foreach ($name in @('tesseract50.dll','leptonica-1.82.0.dll','msvcp140.dll','vcruntime140.dll')) { $required += 'PortableOcr\Native\' + $architecture + '\' + $name }
    }
    $required += 'PortableOcr\Native\x64\vcruntime140_1.dll'
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $projectRoot $relative) -PathType Leaf)) { throw ('Required offline asset is missing: ' + $relative) }
    }
    $metadataPath = $null
    $metadataRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata'
    if (Test-Path -LiteralPath $metadataRoot) {
        $metadataPath = Get-ChildItem -LiteralPath $metadataRoot -Directory |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'Windows.winmd' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    $metadataArgument = @()
    if ($metadataPath) { $metadataArgument = @('/p:WinRTMetadataPath=' + $metadataPath) }
    $offlineFeed = Join-Path $projectRoot 'obj\offline-feed'
    New-Item -ItemType Directory -Path $offlineFeed -Force | Out-Null
    $projectPath = Join-Path $projectRoot 'CratePilot.csproj'
    Invoke-Dotnet -Arguments (@('restore',$projectPath,'--source',$offlineFeed,'--ignore-failed-sources','/p:NuGetAudit=false','/p:AutomaticallyUseReferenceAssemblyPackages=false') + $metadataArgument)
    Invoke-Dotnet -Arguments (@('build',$projectPath,'--configuration','Release','--no-restore','--verbosity','minimal','/p:NuGetAudit=false') + $metadataArgument)
    $after = Get-InputDigest
    Write-Host ('Input SHA256 after: ' + $after)
    if ($before -ne $after) { throw 'Source or embedded assets changed during compilation. No new distribution was published.' }
    $outputPath = Join-Path $projectRoot 'bin\Release\net48'
    $outputExe = Join-Path $outputPath 'CratePilot.exe'
    if (-not (Test-Path -LiteralPath $outputExe)) { throw 'CratePilot.exe was not generated.' }
    if (@(Get-ChildItem -LiteralPath $outputPath -Filter '*.dll' -File).Count -gt 0) { throw 'Unexpected loose application DLL dependencies. Distribution was not published.' }
    New-Item -ItemType Directory -Path $distPath -Force | Out-Null
    $distExe = Join-Path $distPath 'CratePilot.exe'
    Copy-Item -LiteralPath $outputExe -Destination $distExe -Force
    $hash = (Get-FileHash -LiteralPath $distExe -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $distPath 'CratePilot.exe.sha256'), "$hash  CratePilot.exe`r`n", [Text.Encoding]::ASCII)
    Write-Host ('Executable: ' + $distExe)
    Write-Host ('Architecture: ' + [Reflection.AssemblyName]::GetAssemblyName($distExe).ProcessorArchitecture)
    Write-Host ('Bytes: ' + (Get-Item -LiteralPath $distExe).Length)
    Write-Host ('SHA256: ' + $hash)
    $signature = Get-AuthenticodeSignature -LiteralPath $distExe
    Write-Host ('Authenticode: ' + $signature.Status)
    if ($signature.Status -ne 'Valid') { Write-Host 'No verified publisher signature. SmartScreen may warn or block; protections are not disabled.' }
    Write-Host 'BUILD: PASS - compilation/package; no in-application verification suite is shipped or invoked.'
    exit 0
}
catch {
    Write-Host ('BUILD: FAIL - ' + $_.Exception.Message)
    exit 1
}
