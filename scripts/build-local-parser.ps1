param(
    [string]$UpstreamPath = (Join-Path $PSScriptRoot '..\..\FRD'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\local-packages'),
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$packageVersion = '3.0.5-botornot'
$pinnedCommit = '2fc699e99cf8f6654f13fcc5272ea1de57d89fd7'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$upstreamRoot = (Resolve-Path $UpstreamPath).Path
$packageOutput = [System.IO.Path]::GetFullPath($OutputPath)
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('botornot-parser-' + [guid]::NewGuid().ToString('N'))

New-Item -ItemType Directory -Force -Path $packageOutput | Out-Null

try {
    $actualCommit = (& git -C $upstreamRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $pinnedCommit) {
        throw "Expected upstream commit $pinnedCommit, found $actualCommit"
    }

    & git clone --quiet --no-hardlinks $upstreamRoot $temporaryRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not create isolated upstream checkout' }
    & git -C $temporaryRoot checkout --quiet --detach $pinnedCommit
    if ($LASTEXITCODE -ne 0) { throw 'Could not check out pinned upstream commit' }

    $env:DOTNET_CLI_HOME = Join-Path $temporaryRoot '.dotnet-home'
    $env:APPDATA = Join-Path $temporaryRoot '.appdata'
    $env:LOCALAPPDATA = Join-Path $temporaryRoot '.localappdata'
    if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $env:NUGET_PACKAGES = Join-Path $temporaryRoot '.nuget-packages'
    }
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

    & python (Join-Path $repositoryRoot 'patches\apply-patches.py') $temporaryRoot
    if ($LASTEXITCODE -ne 0) { throw 'Parser patch application failed' }

    $nugetConfig = Join-Path $temporaryRoot 'NuGet.config'
    $escapedOutput = [System.Security.SecurityElement]::Escape($packageOutput)
    $config = @"
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$escapedOutput" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@
    Set-Content -LiteralPath $nugetConfig -Value $config

    foreach ($name in @('OozSharp', 'Unreal.Encryption', 'Unreal.Core', 'FortniteReplayReader')) {
        $project = Join-Path $temporaryRoot "src\$name\$name.csproj"
        $content = Get-Content -LiteralPath $project -Raw
        foreach ($dependency in @('OozSharp', 'Unreal.Core', 'Unreal.Encryption')) {
            $projectReference = '<ProjectReference Include="..\' + $dependency + '\' + $dependency + '.csproj" />'
            $packageReference = '<PackageReference Include="' + $dependency + '" Version="' + $packageVersion + '" />'
            $content = $content.Replace($projectReference, $packageReference)
        }
        Set-Content -LiteralPath $project -Value $content -NoNewline

        $packageFile = Join-Path $packageOutput "$name.$packageVersion.nupkg"
        if (Test-Path -LiteralPath $packageFile) {
            Remove-Item -LiteralPath $packageFile -Force
        }

        & $DotnetPath pack $project -c Release -o $packageOutput `
            "-p:PackageVersion=$packageVersion" --configfile $nugetConfig `
            --ignore-failed-sources --nologo
        if ($LASTEXITCODE -ne 0) { throw "Pack failed: $name" }
    }

    Write-Host "Packed parser dependency set $packageVersion to $packageOutput"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = (Resolve-Path $temporaryRoot).Path
        $systemTemporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $resolvedTemporaryRoot.StartsWith($systemTemporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected temporary path: $resolvedTemporaryRoot"
        }
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
