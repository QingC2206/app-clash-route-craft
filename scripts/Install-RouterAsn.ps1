# Run on the user's Windows computer. Password is prompted without echo by Python.
[CmdletBinding()]
param(
    [string]$Router,
    [string]$UserName,
    [ValidateRange(1,65535)][int]$Port = 22,
    [string]$AsnFile,
    [string]$LoginFile,
    [string]$HostKeySha256,
    [string]$KnownHosts,
    [string]$DataDirectory,
    [string]$CorePath,
    [Security.SecureString]$Password,
    [switch]$Plain,
    [switch]$Json
)
$ErrorActionPreference = 'Stop'
$arguments = @('-X', 'utf8', (Join-Path $PSScriptRoot 'install-router-asn.py'))
if ($PSBoundParameters.ContainsKey('Port')) { $arguments += @('--port', "$Port") }
if ($Plain) { $arguments += '--plain' }
if ($Json) { $arguments += '--json' }
foreach ($pair in @(@('--host', $Router), @('--user', $UserName), @('--asn-file', $AsnFile), @('--login-file', $LoginFile), @('--host-key-sha256', $HostKeySha256), @('--known-hosts', $KnownHosts), @('--data-dir', $DataDirectory), @('--core', $CorePath))) {
    if ($pair[1]) { $arguments += $pair }
}
if (Get-Command python -ErrorAction SilentlyContinue) {
    $pythonCommand = 'python'
} elseif (Get-Command py -ErrorAction SilentlyContinue) {
    $pythonCommand = 'py'
    $arguments = @('-3') + $arguments
} else {
    throw 'Python 3.10+ is required on this computer.'
}
if ($Password) {
    $arguments += '--password-stdin'
    $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
    $previousOutputEncoding = $OutputEncoding
    try {
        $OutputEncoding = [Text.UTF8Encoding]::new($false)
        $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
        $plainPassword | & $pythonCommand @arguments
        $toolExitCode = $LASTEXITCODE
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
        $plainPassword = $null
        $OutputEncoding = $previousOutputEncoding
    }
} else {
    & $pythonCommand @arguments
    $toolExitCode = $LASTEXITCODE
}
exit $toolExitCode
