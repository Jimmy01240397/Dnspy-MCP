<#
    builder.ps1

    One-shot build for the whole repo.  Builds the small subset of dnSpy
    projects whose output DLLs the agent references, copies them into
    ./lib/, then builds dnspymcp + dnspymcpagent and stages a distributable
    layout under ./dist/.

    Usage (PowerShell 7+ recommended):
        pwsh -File builder.ps1
        pwsh -File builder.ps1 -Configuration Release -Zip
        pwsh -File builder.ps1 -SkipDnSpy            # lib/ already populated
        pwsh -File builder.ps1 -Clean

    Output layout:
        lib/                     referenced DLLs for dnspymcpagent
        dist/dnspymcp/           net9 MCP server (publish output)
        dist/dnspymcpagent/      net48 agent   (build  output)
        dist/lib/                mirror of lib/ for portable release
        dist/dnspymcp-<ver>-win-x64.zip   if -Zip
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipDnSpy,
    [switch]$Clean,
    [switch]$Zip,
    [string]$Version = $env:GITHUB_REF_NAME
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$libDir = Join-Path $root 'lib'
$distDir = Join-Path $root 'dist'

if ($Clean) {
    foreach ($d in @($libDir, $distDir)) {
        if (Test-Path $d) { Remove-Item -Recurse -Force $d }
    }
    Write-Host "==> cleaned"
    return
}

# ---------- 1. ensure dnspy submodule is checked out ------------------------
$dnspyDir = Join-Path $root 'dnspy'
if (-not (Test-Path (Join-Path $dnspyDir 'dnSpy.sln'))) {
    Write-Host "==> initialising dnspy submodule"
    git -C $root submodule update --init --recursive
}

# ---------- 2. build dnspy subset into ./lib/ -------------------------------
$corDebugProj = Join-Path $dnspyDir 'Extensions/dnSpy.Debugger/dnSpy.Debugger.DotNet.CorDebug/dnSpy.Debugger.DotNet.CorDebug.csproj'
$corDebugOutDir = Join-Path $dnspyDir "dnSpy/dnSpy/bin/$Configuration"

$needed = @(
    'dnSpy.Debugger.DotNet.CorDebug.x.dll',
    'dnSpy.Contracts.Debugger.DotNet.CorDebug.dll',
    'dnSpy.Contracts.Debugger.DotNet.dll',
    'dnSpy.Contracts.Debugger.dll',
    'dnSpy.Contracts.DnSpy.dll',
    'dnSpy.Contracts.Logic.dll',
    'dnSpy.Debugger.DotNet.Metadata.dll',
    'dnlib.dll'
)

function Copy-Libs {
    param([string]$From, [string]$To)
    New-Item -ItemType Directory -Path $To -Force | Out-Null
    foreach ($n in $needed) {
        $hit = Get-ChildItem -Path $From -Filter $n -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $hit) { throw "not found in dnspy build output: $n (looked under $From)" }
        Copy-Item $hit.FullName (Join-Path $To $n) -Force
        foreach ($ext in '.pdb','.xml') {
            $side = [IO.Path]::ChangeExtension($hit.FullName, $ext)
            if (Test-Path $side) { Copy-Item $side (Join-Path $To ([IO.Path]::GetFileName($side))) -Force }
        }
    }
}

if (-not $SkipDnSpy) {
    Write-Host "==> building dnspy CorDebug project ($Configuration)"
    & dotnet build $corDebugProj -c $Configuration -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dnspy build failed" }

    Write-Host "==> staging ./lib/"
    Copy-Libs -From $corDebugOutDir -To $libDir
}
elseif (-not (Test-Path $libDir)) {
    throw "-SkipDnSpy was passed but ./lib/ is missing. Run without -SkipDnSpy once."
}

# ---------- 3. build the two projects --------------------------------------
Write-Host "==> publishing dnspymcp (net9)"
& dotnet publish (Join-Path $root 'dnspymcp/dnspymcp.csproj') -c $Configuration --self-contained false -o (Join-Path $distDir 'dnspymcp')
if ($LASTEXITCODE -ne 0) { throw "dnspymcp build failed" }

Write-Host "==> building dnspymcpagent (net48)"
& dotnet build (Join-Path $root 'dnspymcpagent/dnspymcpagent.csproj') -c $Configuration -o (Join-Path $distDir 'dnspymcpagent')
if ($LASTEXITCODE -ne 0) { throw "dnspymcpagent build failed" }

# ---------- 4. stage lib/ into dist/ ---------------------------------------
Copy-Libs -From $libDir -To (Join-Path $distDir 'lib')

foreach ($doc in 'README.md','LICENSE','CLAUDE.md') {
    $p = Join-Path $root $doc
    if (Test-Path $p) { Copy-Item $p $distDir -Force }
}

# ---------- 5. optional zip ------------------------------------------------
if ($Zip) {
    $tag = if ($Version) { $Version } else { "dev-" + (Get-Date -Format 'yyyyMMdd-HHmmss') }
    $zipPath = Join-Path $distDir "dnspymcp-$tag-win-x64.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "==> zipping -> $zipPath"
    Compress-Archive -Path (Join-Path $distDir '*') -DestinationPath $zipPath -Force
}

Write-Host "==> done"
