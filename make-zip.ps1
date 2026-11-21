param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination
)

# Creates a zip whose entry names always use forward slashes.
# [System.IO.Compression.ZipFile]::CreateFromDirectory on .NET Framework writes
# backslash separators on Windows, which breaks extraction outside Windows.

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = (Resolve-Path -LiteralPath $Source).Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$target = if ([System.IO.Path]::IsPathRooted($Destination)) { $Destination } else { Join-Path (Get-Location).Path $Destination }
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }

$zip = [System.IO.Compression.ZipFile]::Open($target, 'Create')
try {
    Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
        $entry = $zip.CreateEntry($relative)
        $fileStream = [System.IO.File]::OpenRead($_.FullName)
        $entryStream = $entry.Open()
        try { $fileStream.CopyTo($entryStream) } finally { $entryStream.Dispose(); $fileStream.Dispose() }
    }
} finally {
    $zip.Dispose()
}

Write-Output $target
