param(
    [string]$RawDirectory = "data/raw/historical",
    [string]$ManifestPath = "data/manifests/historical-2020-current.json",
    [string]$CurrentSnapshotPath = "data/snapshot/tenderlens-live.db"
)

$ErrorActionPreference = "Stop"
$resources = @(
    @{ Id="fdcc0b4d-f0f6-44f4-b917-638376d1fcf1"; Family="АОП / ЦАИС ЕОП CSV"; Kind="contracts"; Year=2020 },
    @{ Id="3cf24c43-84af-4cf4-818f-3a45b614e939"; Family="АОП / ЦАИС ЕОП CSV"; Kind="amendments"; Year=2020 },
    @{ Id="dc1c36c6-8332-4590-8a31-855cae94d5e0"; Family="АОП / РОП CSV"; Kind="contracts"; Year=2020 },
    @{ Id="8c02b1bf-bf31-413a-8492-6ad9da7deee2"; Family="АОП / РОП CSV"; Kind="amendments"; Year=2020 },
    @{ Id="ec252e14-71e9-4448-8227-7cae01caada9"; Family="АОП / ЦАИС ЕОП CSV"; Kind="contracts"; Year=2021 },
    @{ Id="1c1a3e5f-9b75-447b-83e3-a639a4854e20"; Family="АОП / ЦАИС ЕОП CSV"; Kind="amendments"; Year=2021 },
    @{ Id="0fadfb4a-7361-47ac-a455-7b490ea4e73a"; Family="АОП / РОП CSV"; Kind="contracts"; Year=2021 },
    @{ Id="6a350fc7-7fd8-4696-9a16-ffb2b5d9d5bb"; Family="АОП / РОП CSV"; Kind="amendments"; Year=2021 },
    @{ Id="03e63b8c-c0e4-4e44-a777-3a751c3f735d"; Family="АОП / ЦАИС ЕОП CSV"; Kind="contracts"; Year=2022 },
    @{ Id="2daf2219-b98f-49c3-863b-e89f84a2fddd"; Family="АОП / ЦАИС ЕОП CSV"; Kind="amendments"; Year=2022 },
    @{ Id="181a183f-aec9-46de-a724-48cf5f7a5362"; Family="АОП / РОП CSV"; Kind="contracts"; Year=2022 },
    @{ Id="958f2f3e-d429-4314-a9e4-6ab4202dbc16"; Family="АОП / РОП CSV"; Kind="amendments"; Year=2022 },
    @{ Id="0809e6c3-90b2-4fcd-889c-9130f8dabfbe"; Family="АОП / ЦАИС ЕОП CSV"; Kind="contracts"; Year=2023 },
    @{ Id="d67457ff-86f0-4501-90db-65497336d3cd"; Family="АОП / ЦАИС ЕОП CSV"; Kind="amendments"; Year=2023 },
    @{ Id="34a9066f-d467-4921-a427-d41447bb2d8c"; Family="АОП / РОП CSV"; Kind="contracts"; Year=2023 },
    @{ Id="8f04fb86-e307-4f4e-9a1d-fb967e4dcbaa"; Family="АОП / РОП CSV"; Kind="amendments"; Year=2023 },
    @{ Id="dcd45fa9-eaec-4ee9-a908-054fada944b1"; Family="АОП / РОП CSV"; Kind="contracts"; Year=2024 },
    @{ Id="75eecda0-2313-4301-9669-1c8421cc48e0"; Family="АОП / РОП CSV"; Kind="amendments"; Year=2024 },
    @{ Id="94c99c57-a07b-4838-aba2-ab085e247aca"; Family="АОП / РОП CSV"; Kind="contracts"; Year=2025 },
    @{ Id="2e1c7c3f-d4c7-49be-92f3-eff90ede1da1"; Family="АОП / РОП CSV"; Kind="amendments"; Year=2025 }
)

New-Item -ItemType Directory -Force $RawDirectory | Out-Null
$retrievedAt = [DateTimeOffset]::UtcNow.ToString("o")
$manifestDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $ManifestPath))
$entries = foreach ($resource in $resources) {
    $url = "https://data.egov.bg/resource/download/$($resource.Id)/csv"
    $path = Join-Path $RawDirectory "$($resource.Year)-$($resource.Kind)-$($resource.Id).csv"
    Invoke-WebRequest -UseBasicParsing $url -OutFile $path
    $item = Get-Item $path
    $baseUri = [Uri]($manifestDirectory.TrimEnd('\') + '\')
    $relative = [Uri]::UnescapeDataString($baseUri.MakeRelativeUri([Uri]$item.FullName).ToString())
    [ordered]@{ id=$resource.Id; family=$resource.Family; kind=$resource.Kind; year=$resource.Year; url=$url; localPath=$relative; retrievedAt=$retrievedAt; bytes=$item.Length; sha256=(Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant(); required=$true }
}
$current = Get-Item $CurrentSnapshotPath
$baseUri = [Uri]($manifestDirectory.TrimEnd('\') + '\')
$currentRelative = [Uri]::UnescapeDataString($baseUri.MakeRelativeUri([Uri]$current.FullName).ToString())
$entries += [ordered]@{
    id="ocds-current-$([DateTime]::UtcNow.Year)"; family="AOP / CAIS EOP OCDS"; kind="snapshot"; year=[DateTime]::UtcNow.Year
    url="https://data.egov.bg"; localPath=$currentRelative; retrievedAt=$retrievedAt; bytes=$current.Length
    sha256=(Get-FileHash -Algorithm SHA256 $current).Hash.ToLowerInvariant(); required=$true
}
[ordered]@{ schemaVersion="1"; mode="historicalBackfill"; resources=$entries } | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $ManifestPath
Write-Host "Acquired $($entries.Count) checksum-bound resources and wrote $ManifestPath"
