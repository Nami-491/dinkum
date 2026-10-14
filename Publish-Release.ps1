param(
    [Parameter(Mandatory=$true)][string]$Package,
    [string]$Tag = 'v4.2.0'
)
$ErrorActionPreference = 'Stop'
$repository = 'Nami-491/dinkum'
if ($Tag -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Use a version tag such as v4.2.0.' }
$archive = (Resolve-Path -LiteralPath $Package).Path
if ([IO.Path]::GetExtension($archive) -ne '.zip') { throw 'Select the verified distribution ZIP, not a save or game folder.' }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is required.' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.git'))) { throw 'Run this from the prepared Git repository, with README committed.' }
$remote = & git -C $PSScriptRoot remote get-url origin
if ($LASTEXITCODE -ne 0 -or $remote -ne "https://github.com/$repository.git") { throw 'Unexpected origin; no files were uploaded.' }
$changes = & git -C $PSScriptRoot status --porcelain
if ($changes) { throw 'Review and commit intended README/docs changes before publishing.' }
# Use the normal Git credential helper. Keep credentials in memory only; do not
# print them, put them in command-line arguments, or save them in the repository.
$oldPrompt = $env:GIT_TERMINAL_PROMPT
$oldInteractive = $env:GCM_INTERACTIVE
$credentialLines = $null; $tokenValue = $null; $headers = $null
try {
    $env:GIT_TERMINAL_PROMPT = '0'; $env:GCM_INTERACTIVE = 'never'
    $credentialLines = "protocol=https`nhost=github.com`npath=$repository.git`n`n" | & git -C $PSScriptRoot credential fill
    if ($LASTEXITCODE -ne 0) { throw 'Sign in to GitHub using your Git credential manager first.' }
    $secretLine = @($credentialLines | Where-Object { $_.StartsWith('password=') })
    if ($secretLine.Count -ne 1) { throw 'No GitHub credential was available.' }
    $tokenValue = $secretLine[0].Substring(9)
    $headers = @{ Authorization = "Bearer $tokenValue"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2026-03-10'; 'User-Agent' = 'Dinkum-Modpack-Publisher' }
    $api = "https://api.github.com/repos/$repository"
    $repo = Invoke-RestMethod -Uri $api -Headers $headers
    if (-not $repo.permissions.push) { throw 'The Git credential account does not have push permission.' }
    $existing = Invoke-RestMethod -Uri "$api/releases?per_page=100" -Headers $headers
    if (@($existing | Where-Object tag_name -eq $Tag).Count -gt 0) { throw 'Release tag already exists. Existing assets will not be overwritten.' }
    & git -C $PSScriptRoot push origin main
    if ($LASTEXITCODE -ne 0) { throw 'Push failed. Existing remote commits are never overwritten automatically.' }
    $commit = (& git -C $PSScriptRoot rev-parse HEAD).Trim()
    $digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $notes = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'RELEASE-NOTES.md')) + "`n`nZIP SHA-256: ``$digest```n"
    $body = @{tag_name=$Tag; target_commitish=$commit; name="Dinkum Modpack $Tag 테스트 배포"; body=$notes; draft=$true; prerelease=$true} | ConvertTo-Json
    $release = Invoke-RestMethod -Uri "$api/releases" -Headers $headers -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    $assetName = [Uri]::EscapeDataString([IO.Path]::GetFileName($archive))
    $upload = "https://uploads.github.com/repos/$repository/releases/$($release.id)/assets?name=$assetName"
    $asset = Invoke-RestMethod -Uri $upload -Headers $headers -Method Post -ContentType 'application/zip' -InFile $archive
    if ($asset.state -ne 'uploaded' -or $asset.size -ne (Get-Item -LiteralPath $archive).Length -or $asset.digest -ne "sha256:$digest") {
        throw 'Uploaded asset verification failed; release remains a draft for inspection.'
    }
    $published = Invoke-RestMethod -Uri "$api/releases/$($release.id)" -Headers $headers -Method Patch -ContentType 'application/json' -Body '{"draft":false,"prerelease":true}'
    $verified = Invoke-RestMethod -Uri "$api/releases/$($release.id)" -Headers $headers
    if ($verified.draft -or -not $verified.prerelease -or @($verified.assets | Where-Object { $_.id -eq $asset.id -and $_.digest -eq "sha256:$digest" }).Count -ne 1) { throw 'Final release verification failed.' }
    [PSCustomObject]@{Release=$published.html_url; Download=$asset.browser_download_url; SHA256=$digest; AssetId=$asset.id}
}
finally {
    if ($headers) { $headers.Clear() }
    $credentialLines = $null; $secretLine = $null; $tokenValue = $null
    $env:GIT_TERMINAL_PROMPT = $oldPrompt; $env:GCM_INTERACTIVE = $oldInteractive
}
