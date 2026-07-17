param(
    [string]$SnapshotPath = "data/snapshot/tenderlens-historical.db",
    [string]$ArchivePath = "data/releases/tenderlens-historical.db.gz"
)
$ErrorActionPreference = "Stop"
if (!(Test-Path $SnapshotPath)) { throw "Snapshot not found: $SnapshotPath" }
New-Item -ItemType Directory -Force (Split-Path -Parent $ArchivePath) | Out-Null
$input = [IO.File]::OpenRead((Resolve-Path $SnapshotPath))
try {
    $output = [IO.File]::Create($ArchivePath)
    try { $gzip = New-Object IO.Compression.GZipStream($output,[IO.Compression.CompressionMode]::Compress); try { $input.CopyTo($gzip) } finally { $gzip.Dispose() } } finally { $output.Dispose() }
} finally { $input.Dispose() }
$source = Get-Item $SnapshotPath; $archive = Get-Item $ArchivePath
[ordered]@{ snapshot=$source.FullName; snapshotBytes=$source.Length; snapshotSha256=(Get-FileHash -Algorithm SHA256 $source).Hash.ToLowerInvariant(); archive=$archive.FullName; archiveBytes=$archive.Length; archiveSha256=(Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant(); compressionRatio=[Math]::Round($archive.Length/$source.Length,4) } | ConvertTo-Json
