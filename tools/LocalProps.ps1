# Reads machine-specific paths from the repo's git-ignored Local.props (see Local.props.example).
# Dot-source it, then call Get-LocalProp <Name>; it returns $null when the file or the value is missing.

$script:LocalPropsFile = Join-Path (Split-Path -Parent $PSScriptRoot) "Local.props"

function Get-LocalProp([string]$Name) {
    if (-not (Test-Path $script:LocalPropsFile)) { return $null }
    $node = ([xml](Get-Content $script:LocalPropsFile -Raw)).SelectSingleNode("//*[local-name()='$Name']")
    if ($node -and $node.InnerText.Trim()) { return $node.InnerText.Trim() }
    return $null
}
