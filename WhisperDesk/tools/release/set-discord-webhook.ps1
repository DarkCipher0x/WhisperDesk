# Saves the #whisperdesk Discord webhook URL in Windows Credential Manager (target DarkCipher/discord/whisperdesk), where
# release.ps1 reads it. Run it yourself; the URL is typed hidden and never written to a file or shown.
#   powershell -ExecutionPolicy Bypass -File tools\release\set-discord-webhook.ps1
# In Discord: the channel's settings (gear) > Integrations > Webhooks > New Webhook > Copy Webhook URL.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$have = [DcxCred]::Read($WebhookTarget)
Write-Host "Webhook for #whisperdesk: $(if ($have) { 'saved already (Enter keeps it)' } else { 'not set' })"
while ($true) {
    $sec = Read-Host 'Paste the webhook URL' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try { $url = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    if (-not $url) { break }
    if ($url -match $WebhookRx) { [DcxCred]::Write($WebhookTarget, 'discord-webhook', $url); Write-Host 'Saved in Windows Credential Manager.' -ForegroundColor Green; break }
    Write-Host "That isn't a Discord webhook URL (https://discord.com/api/webhooks/...). Try again." -ForegroundColor Yellow
}
Remove-Variable url -ErrorAction SilentlyContinue
