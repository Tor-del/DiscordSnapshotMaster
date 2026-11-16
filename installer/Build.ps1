param(
    [string]$Compiler = 'ISCC.exe',
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '1.2.2',
    [string]$PythonArchive
)

$ErrorActionPreference = 'Stop'
$compilerPath = (Get-Command $Compiler -CommandType Application).Source
$project = Join-Path $PSScriptRoot '..\gui\DiscordSnapshot.Gui.csproj'
$definition = Join-Path $PSScriptRoot 'Setup.iss'
$stagingRoot = New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'obj') -Force
$publish = New-Item -ItemType Directory -Path (Join-Path $stagingRoot.FullName ([guid]::NewGuid().ToString('N')))

try {
    $publishArgs = @(
        'publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', '-o', $publish.FullName,
        "-p:Version=$Version", '-p:DebugType=None', '-p:DebugSymbols=false', '-p:TargetPlatformDisplayName=Windows'
    )
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $runtime = Join-Path $publish.FullName 'python'
    if ($PythonArchive) {
        Expand-Archive -LiteralPath $PythonArchive -DestinationPath $runtime
    }
    else {
        $pythonVersion = '3.13.17'
        $download = Join-Path $publish.FullName 'python-embed.zip'
        Invoke-WebRequest -UseBasicParsing -Uri "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embed-amd64.zip" -OutFile $download
        Expand-Archive -LiteralPath $download -DestinationPath $runtime
        Remove-Item -LiteralPath $download
    }
    & (Join-Path $runtime 'python.exe') -I -B -c 'import http.server, sqlite3, sys; assert sys.version_info >= (3, 10)'
    if ($LASTEXITCODE -ne 0) { throw 'The bundled Python runtime could not load the archive viewer dependencies.' }

    & $compilerPath '/Qp' "/DPublishDir=$($publish.FullName)" "/DAppVersion=$Version" $definition
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
}
finally {
    $resolvedPublish = (Resolve-Path -LiteralPath $publish.FullName).ProviderPath
    $resolvedStaging = (Resolve-Path -LiteralPath $stagingRoot.FullName).ProviderPath + [IO.Path]::DirectorySeparatorChar
    if ($resolvedPublish.StartsWith($resolvedStaging, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
    }
}
