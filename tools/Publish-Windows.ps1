param([switch]$ProbeOnly)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$env:GIT_TERMINAL_PROMPT='0'
$env:GCM_INTERACTIVE='never'
# Use the user's existing Git Credential Manager session; never print/store its token.
$credential="protocol=https`nhost=github.com`n`n" | git -C $root credential fill 2>$null
if($LASTEXITCODE -ne 0){throw 'GitHub authentication unavailable. Sign in to GitHub using Git Credential Manager first.'}
$password=$credential | Where-Object {$_ -like 'password=*'} | Select-Object -First 1
if(!$password){throw 'No GitHub credential returned'}
$headers=@{Authorization=('Bearer '+$password.Substring(9));Accept='application/vnd.github+json';'User-Agent'='CallOfAI-ReleaseTool'}
$api='https://api.github.com/repos/gregoryleonardclarke/call-of-ai-launcher'
$repo=Invoke-RestMethod -Uri $api -Headers $headers
if(!$repo.permissions.push){throw 'Authenticated account cannot publish to this repository'}
Write-Output "Repository verified; push permission confirmed; public=$(-not $repo.private)"
if($ProbeOnly){return}
$assets=@('CallOfAI-Launcher-0.2.0-win-x64.zip','CallOfAI-Game-0.1.1-win-x64.zip','launcher-win-x64.json','game-win-x64.json')
foreach($name in $assets){if(!(Test-Path (Join-Path "$root\artifacts\release" $name))){throw "Missing release asset: $name"}}
$releases=Invoke-RestMethod -Uri "$api/releases?per_page=100" -Headers $headers
$release=$releases | Where-Object {$_.tag_name -eq 'stable'} | Select-Object -First 1
if($release -and !$release.draft){throw 'Stable release already public: use an explicitly reviewed update workflow instead.'}
if(!$release){
 $body=@{tag_name='stable';target_commitish='main';name='Call of AI - Windows test release';draft=$true;prerelease=$false;body="Windows x64 test build. Download CallOfAI-Launcher-0.2.0-win-x64.zip, extract the ENTIRE folder, and run CallOfAILauncher.exe. The launcher installs the game and detects future published game/launcher updates. Unsigned Windows executable. macOS/Linux are not included. This is an early test build, not a production competitive multiplayer service."} | ConvertTo-Json
 $release=Invoke-RestMethod -Uri "$api/releases" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
}
foreach($name in $assets){
 $existing=$release.assets | Where-Object {$_.name -eq $name}
 if($existing){throw "Draft already contains $name; inspect before retrying to avoid accidental overwrite"}
 $url=$release.upload_url.Split('{')[0]+'?name='+[uri]::EscapeDataString($name)
 $result=Invoke-RestMethod -Uri $url -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile (Join-Path "$root\artifacts\release" $name) -TimeoutSec 1800
 Write-Output "Uploaded $($result.name) ($($result.size) bytes)"
}
$result=Invoke-RestMethod -Uri "$api/releases/$($release.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body '{"draft":false,"make_latest":"true"}'
Write-Output "Published: $($result.html_url)"
$headers.Clear();$credential=$null;$password=$null