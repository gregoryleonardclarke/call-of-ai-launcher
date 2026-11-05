param([Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,[switch]$UploadOnly)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder="$root\artifacts\launcher-$Version"
$out="$root\artifacts\launcher-release-$Version"
New-Item -ItemType Directory -Force $folder,$out | Out-Null
$project=[xml](Get-Content "$root\src\Launcher.csproj" -Raw)
if([version]$project.Project.PropertyGroup.Version -ne [version]$Version){throw 'Requested version must match launcher project version'}
if(!$UploadOnly){
& dotnet publish "$root\src\Launcher.csproj" -c Release -r win-x64 --self-contained true -o $folder
if($LASTEXITCODE -ne 0){throw 'Launcher build failed'}
$name="CallOfAI-Launcher-$Version-win-x64.zip"
$base='https://github.com/gregoryleonardclarke/call-of-ai-launcher/releases/download/stable/'
& dotnet run --project "$root\tools\ReleaseTool.csproj" -- pack $folder CallOfAILauncher.exe $Version win-x64 ($base+$name) 'C:\Users\gregl\.call-of-ai-signing\release.private.pem' "$out\$name" "$out\launcher-win-x64.json" launcher 'FRONTLINE launcher branding. Existing game installations and updates are preserved.'
if($LASTEXITCODE -ne 0){throw 'Packaging failed'}
}
$name="CallOfAI-Launcher-$Version-win-x64.zip"
$base='https://github.com/gregoryleonardclarke/call-of-ai-launcher/releases/download/stable/'
$env:GIT_TERMINAL_PROMPT='0'; $env:GCM_INTERACTIVE='never'
$ErrorActionPreference='Continue'
$credential="protocol=https`nhost=github.com`n`n" | git -C $root credential fill 2>$null
$ErrorActionPreference='Stop'
if($LASTEXITCODE -ne 0){throw 'GitHub authentication failed'}
$password=$credential | Where-Object {$_ -like 'password=*'} | Select-Object -First 1
if(!$password){throw 'No GitHub token available'}
$headers=@{Authorization=('Bearer '+$password.Substring(9));Accept='application/vnd.github+json';'User-Agent'='CallOfAI-ReleaseTool'}
$api='https://api.github.com/repos/gregoryleonardclarke/call-of-ai-launcher'
$release=Invoke-RestMethod "$api/releases/tags/stable" -Headers $headers
if($release.assets | Where-Object {$_.name -eq $name}){throw 'Immutable version ZIP already exists; inspect before retrying'}
$upload=$release.upload_url.Split('{')[0]
$asset=Invoke-RestMethod ($upload+'?name='+$name) -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile "$out\$name" -TimeoutSec 1800
if($asset.size -ne (Get-Item "$out\$name").Length){throw 'Uploaded ZIP size mismatch'}
Write-Output "ZIP uploaded: $name"
# Upload and validate the new manifest before changing the stable pointer.
$pending="launcher-$Version-win-x64.json"
$candidate=Invoke-RestMethod ($upload+'?name='+$pending) -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile "$out\launcher-win-x64.json"
$previous=$release.assets | Where-Object {$_.name -eq 'launcher-win-x64.json'} | Select-Object -First 1
if($previous){
 Invoke-WebRequest -UseBasicParsing $previous.browser_download_url -OutFile "$out\previous-launcher-win-x64.json"
 # Preserve rollback manifest under a unique name rather than deleting it.
 $backup='launcher-previous-'+$previous.id+'.json'
 Invoke-RestMethod "$api/releases/assets/$($previous.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body (@{name=$backup}|ConvertTo-Json) | Out-Null
}
try {
 Invoke-RestMethod "$api/releases/assets/$($candidate.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body '{"name":"launcher-win-x64.json"}' | Out-Null
} catch {
 if($previous){Invoke-RestMethod "$api/releases/assets/$($previous.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body '{"name":"launcher-win-x64.json"}' | Out-Null}
 throw
}
Invoke-WebRequest -UseBasicParsing ($base+'launcher-win-x64.json?version='+$Version) -OutFile "$out\public-manifest.json"
if((Get-FileHash "$out\public-manifest.json").Hash -ne (Get-FileHash "$out\launcher-win-x64.json").Hash){throw 'Public manifest mismatch'}
Write-Output "PUBLISHED AND VERIFIED launcher $Version"
$headers.Clear();$credential=$null;$password=$null