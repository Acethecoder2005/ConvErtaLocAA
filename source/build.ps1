param(
    [string]$PythonPath = 'python',
    [string]$Destination = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $repoRoot 'dist\ConvErtaLocAA' }
$destinationPath = [IO.Path]::GetFullPath($Destination)
& $PythonPath -B (Join-Path $repoRoot 'tools\package_windows.py') $destinationPath
if ($LASTEXITCODE -ne 0) { throw 'Packaging the private runtime failed.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework C# compiler was not found.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /win32icon:"$destinationPath\assets\converter.ico" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /out:"$destinationPath\ConvErtaLocAA.exe" (Join-Path $repoRoot 'source\ConvErtaLocAA.cs')
if ($LASTEXITCODE -ne 0) { throw 'The Windows app did not compile.' }
Write-Output "Built $destinationPath\ConvErtaLocAA.exe"
