$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build '.\Yale.SprayProcessSCADASystem.sln' --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. See the errors above.' }
    $appDirectory = Join-Path $PSScriptRoot 'Yale.SprayProcessSCADASystem\bin\Debug\net10.0-windows'
    $appPath = Join-Path $appDirectory 'Yale.SprayProcessSCADASystem.exe'
    Start-Process -FilePath $appPath -WorkingDirectory $appDirectory -WindowStyle Normal
}
finally {
    Pop-Location
}
