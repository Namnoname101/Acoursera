$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$install = 'D:\Acose\CustomerManager'
$manager = Join-Path $repo 'artifacts\activity-skips-manager'
$engine = Join-Path $repo 'src\AutomationPlatform.Presentation\bin\Release\net10.0-windows\win-x64'
$source = Join-Path $repo 'distribution\customer-manager\src\InstalledManager'
function Assert-Ordinary([string]$path) {
    $cursor = [IO.Path]::GetFullPath($path)
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and
            ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Linked update path refused: $cursor"
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
function Assert-Closed {
    $running = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($running.Count) { throw 'Close CustomerManager and Workers before updating.' }
    $task = Get-ScheduledTask -TaskName 'ACOSE Worker Host' -ErrorAction SilentlyContinue
    if ($task -and $task.State -eq 'Running') { throw 'Worker Host must be stopped before updating.' }
}
foreach ($path in @($install,$manager,$engine,$source)) { Assert-Ordinary $path }
if (-not (Test-Path -LiteralPath (Join-Path $install '.acose-customer-manager-install'))) { throw 'Installation marker missing.' }
Assert-Closed
$expected = @{
    'Acose.CustomerManager.dll'='70413A2420024F3409D7170E7A1940BDAF77636893F0594C597337D48CD523C1'
    'Engine\AutomationPlatform.Presentation.dll'='8018F500F110C7DA004B89C68328272D2A88DD954431F4310F06B4D9B6A501D5'
}
foreach ($name in $expected.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $install $name)).Hash -ne $expected[$name]) { throw "Installed baseline changed: $name" }
}
$copies = @()
foreach ($name in @('Acose.CustomerManager.exe','Acose.CustomerManager.dll','Acose.CustomerManager.pdb','Acose.CustomerManager.deps.json','Acose.CustomerManager.runtimeconfig.json')) {
    $copies += @{ From=Join-Path $manager $name; Relative=$name }
}
foreach ($name in @('AutomationPlatform.Presentation.dll','AutomationPlatform.Presentation.pdb')) {
    $copies += @{ From=Join-Path $engine $name; Relative=Join-Path 'Engine' $name }
}
foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs','.xaml','.csproj')
}) {
    $copies += @{ From=$file.FullName; Relative=Join-Path 'Source' ([IO.Path]::GetRelativePath($source,$file.FullName)) }
}
foreach ($copy in $copies) {
    if (-not (Test-Path -LiteralPath $copy.From -PathType Leaf)) { throw "Candidate missing: $($copy.From)" }
    Assert-Ordinary (Join-Path $install $copy.Relative)
    $copy.Hash = (Get-FileHash -LiteralPath $copy.From).Hash
}
$protected = @{}
foreach ($folder in @('Data','PreparedPdfs','GeneratedPdfs')) {
    $path = Join-Path $install $folder
    if (Test-Path -LiteralPath $path) {
        # Browser caches under Data/Profiles can be many GB. The deployment
        # allowlist never targets them; hash the account/settings files in Data
        # and all documents instead of reading every browser cache entry.
        $files = if ($folder -eq 'Data') { Get-ChildItem -LiteralPath $path -File }
                 else { Get-ChildItem -LiteralPath $path -Recurse -File }
        foreach ($file in $files) {
            $protected[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash
        }
    }
}
$update = Join-Path $install ('Updates\activity-skips-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
Assert-Ordinary $update
$backup = Join-Path $update 'Before'
New-Item -ItemType Directory -Path $backup | Out-Null
$manifestPath = Join-Path $install 'engine-manifest.json'
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $backup 'engine-manifest.json')
foreach ($copy in $copies) {
    $target = Join-Path $install $copy.Relative
    $copy.Existed = Test-Path -LiteralPath $target -PathType Leaf
    if ($copy.Existed) {
        $save = Join-Path $backup $copy.Relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($save)) -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination $save
    }
}
Assert-Closed
try {
    foreach ($copy in $copies) {
        $target = Join-Path $install $copy.Relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $copy.From -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target).Hash -ne $copy.Hash) { throw "Updated hash mismatch: $($copy.Relative)" }
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($entry in $manifest.Files | Where-Object File -eq 'AutomationPlatform.Presentation.dll') {
        $entry.SHA256 = (Get-FileHash -LiteralPath (Join-Path $install ('Engine\' + $entry.File))).Hash
    }
    $manifest | Add-Member -NotePropertyName ActivitySkipsUpdate -NotePropertyValue ([pscustomobject]@{
        InstalledAt=[DateTimeOffset]::Now.ToString('o'); ManagerVersion='1.1.2'; Backup=$backup
        Source=$source; Checks='20 option/launcher checks, 23 import checks, 17 course scanner checks, WPF preview render'
    }) -Force
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 20))
    foreach ($path in $protected.Keys) {
        if ((Get-FileHash -LiteralPath $path).Hash -ne $protected[$path]) { throw 'Customer data changed during update.' }
    }
} catch {
    foreach ($copy in $copies) {
        $target = Join-Path $install $copy.Relative
        if ($copy.Existed) { Copy-Item -LiteralPath (Join-Path $backup $copy.Relative) -Destination $target -Force }
        elseif (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target }
    }
    Copy-Item -LiteralPath (Join-Path $backup 'engine-manifest.json') -Destination $manifestPath -Force
    throw
}
$report = [pscustomobject]@{
    Result='Updated'; Install=$install; Backup=$backup; ProtectedFiles=$protected.Count
    ProtectedFilesUnchanged=$true
    ManagerSha256=(Get-FileHash -LiteralPath (Join-Path $install 'Acose.CustomerManager.dll')).Hash
    EngineSha256=(Get-FileHash -LiteralPath (Join-Path $install 'Engine\AutomationPlatform.Presentation.dll')).Hash
}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $update 'deployment-result.json') -Encoding utf8
$report | ConvertTo-Json
