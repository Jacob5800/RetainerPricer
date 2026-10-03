param(
    [Parameter(Mandatory = $true)]
    [string] $ManifestPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.InternalName -ne 'RetainerPricer' -or [string]::IsNullOrWhiteSpace($manifest.AssemblyVersion)) {
    throw 'The packaged Retainer Pricer manifest is missing its identity or assembly version.'
}
if ([string]::IsNullOrWhiteSpace($manifest.Changelog)) {
    throw 'Set Changelog in RetainerPricer.json before publishing a release.'
}

$download = 'https://github.com/Jacob5800/RetainerPricer/releases/latest/download/latest.zip'
$entry = [ordered]@{
    Author = $manifest.Author
    Name = $manifest.Name
    InternalName = $manifest.InternalName
    AssemblyVersion = $manifest.AssemblyVersion
    TestingAssemblyVersion = $null
    RepoUrl = 'https://github.com/Jacob5800/RetainerPricer'
    ApplicableVersion = $manifest.ApplicableVersion
    DalamudApiLevel = $manifest.DalamudApiLevel
    Punchline = $manifest.Punchline
    Description = $manifest.Description
    Changelog = $manifest.Changelog
    IsHide = $false
    IsTestingExclusive = $false
    DownloadLinkInstall = $download
    DownloadLinkUpdate = $download
    LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
}

ConvertTo-Json -InputObject @($entry) -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
