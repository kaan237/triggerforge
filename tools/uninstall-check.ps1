[CmdletBinding()]
param([switch] $ExpectNoData)

$ErrorActionPreference = 'Stop'
$problems = [Collections.Generic.List[string]]::new()
$uninstall = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
$setupKey = "$uninstall\{D11C945E-623E-42EB-B0DD-3D38E2711163}_is1"
$usbipKey = "$uninstall\{199505b0-b93d-4521-a8c7-897818e0205a}_is1"
$services = 'SYSTEM\CurrentControlSet\Services'
$programFiles = $env:ProgramW6432
if (-not $programFiles) { $programFiles = $env:ProgramFiles }

function Get-RegistryValue($Hive, $View, [string] $Key, [string] $Name) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive, $View)
    try {
        $entry = $base.OpenSubKey($Key, $false)
        if ($entry) {
            try { return $entry.GetValue($Name) }
            finally { $entry.Dispose() }
        }
    }
    finally { $base.Dispose() }
}

function Test-RegistryKey([string] $Key) {
    Test-Path -LiteralPath "Registry::HKEY_LOCAL_MACHINE\$Key"
}

function Report-Path([string] $Path, [bool] $IsProblem) {
    if (Test-Path -LiteralPath $Path) {
        if ($IsProblem) { $problems.Add("Still present: $Path") }
        else { Write-Host "Data kept: $Path" }
    }
    else { Write-Host "Absent: $Path" }
}

try {
    $registeredPath = Get-RegistryValue LocalMachine Registry64 $setupKey 'InstallLocation'
    if ($registeredPath -or (Get-RegistryValue LocalMachine Registry64 $setupKey 'DisplayName')) {
        $problems.Add('The TriggerForge uninstall entry is still present.')
    }
    $installPaths = @((Join-Path $programFiles 'TriggerForge'), $registeredPath) |
        Where-Object { $_ } | Select-Object -Unique
    foreach ($directory in $installPaths) { Report-Path $directory $true }

    $run = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    foreach ($name in 'TriggerForge', 'Tactile') {
        $value = Get-RegistryValue CurrentUser Registry64 $run $name
        if ($null -ne $value) { $problems.Add("Run value still present: HKCU $name") }
        else { Write-Host "Run value absent: HKCU $name" }
    }

    foreach ($folder in 'Programs', 'CommonPrograms') {
        $directory = [Environment]::GetFolderPath($folder, 'DoNotVerify')
        foreach ($shortcut in 'TriggerForge.lnk', 'TriggerForge — Restore controller.lnk',
            'TriggerForge — Kontrolcüyü geri getir.lnk', 'Tactile — Kontrolcüyü geri getir.lnk') {
            Report-Path (Join-Path $directory $shortcut) $true
        }
    }
    Report-Path (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'TriggerForge.lnk') $true
    foreach ($directory in $env:APPDATA, $env:LOCALAPPDATA) {
        Report-Path (Join-Path $directory 'TriggerForge') $ExpectNoData.IsPresent
        Report-Path (Join-Path $directory 'Tactile') $ExpectNoData.IsPresent
    }
}
catch {
    $problems.Add("Could not complete file or registry checks: $($_.Exception.Message)")
}

try {
    if (Test-RegistryKey "$services\HidHide") {
        # The vendor CLI can change its own allowlist entry even during a list command.
        # Use the driver's two read-only list operations directly.
        if (-not ('TriggerForgeDiagnostics.HidHideLists' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TriggerForgeDiagnostics
{
    public static class HidHideLists
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input,
            uint inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);

        public static string[][] Read()
        {
            using (var device = CreateFile(@"\\.\HidHide", 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (device.IsInvalid)
                    throw new Win32Exception();

                return new[] { ReadList(device, 0x80016008), ReadList(device, 0x80016000) };
            }
        }

        private static string[] ReadList(SafeFileHandle device, uint code)
        {
            int length;
            if (!DeviceIoControl(device, code, IntPtr.Zero, 0, null, 0, out length, IntPtr.Zero))
                throw new Win32Exception();
            if (length == 0)
                return new string[0];
            if (length < 0 || length > 1048576 || length % 2 != 0)
                throw new InvalidOperationException("Invalid HidHide list size.");

            var buffer = new byte[length];
            int received;
            if (!DeviceIoControl(device, code, IntPtr.Zero, 0, buffer, buffer.Length, out received, IntPtr.Zero))
                throw new Win32Exception();
            if (received < 0 || received > buffer.Length || received % 2 != 0)
                throw new InvalidOperationException("Invalid HidHide list response.");

            return Encoding.Unicode.GetString(buffer, 0, received)
                .Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
'@
        }
        $lists = [TriggerForgeDiagnostics.HidHideLists]::Read()
        foreach ($device in $lists[0]) {
            if ($device -match 'VID(?:_|&(?:0002)?)054C[&_]PID(?:_|&)(?:0CE6|0DF2)(?=[&\\]|$)') {
                $problems.Add("HidHide hidden DualSense entry: $device")
            }
        }
        foreach ($application in $lists[1]) {
            if ($application -match '(?:^|\\)(?:TriggerForge|Tactile)\.exe$') {
                $problems.Add("HidHide application entry: $application")
            }
        }
        Write-Host "HidHide checked: $($lists[0].Length) device and $($lists[1].Length) application entries."
    }
    else { Write-Host 'HidHide driver is absent.' }
}
catch {
    $problems.Add("Could not read HidHide lists: $($_.Exception.GetBaseException().Message)")
}

try {
    $usbipVersion = Get-RegistryValue LocalMachine Registry64 $usbipKey 'DisplayVersion'
    if ($usbipVersion -or (Test-RegistryKey "$services\usbip2_ude")) {
        $usbipDirectory = Get-RegistryValue LocalMachine Registry64 $usbipKey 'InstallLocation'
        if (-not $usbipDirectory) { $usbipDirectory = Join-Path $programFiles 'USBip' }
        $usbip = Join-Path $usbipDirectory 'usbip.exe'
        if (-not (Test-Path -LiteralPath $usbip -PathType Leaf)) {
            throw "usbip.exe was not found at $usbip."
        }
        $output = & $usbip port
        if ($LASTEXITCODE -ne 0) { throw "usbip port exited with $LASTEXITCODE." }
        $ports = @($output | Where-Object { $_ -match '^\s*Port\s+\d+\s*:' })
        if ($ports.Count -eq 0 -and ($output -join '').Trim()) {
            throw 'Unexpected output from usbip port.'
        }
        foreach ($port in $ports) { $problems.Add("USB/IP attachment: $port") }
        Write-Host "USB/IP attached port count: $($ports.Count)"
    }
    else { Write-Host 'usbip-win2 is absent.' }
}
catch {
    $problems.Add("Could not check USB/IP ports: $($_.Exception.Message)")
}

if ($problems.Count) {
    $problems | ForEach-Object { Write-Warning $_ }
    exit 1
}
Write-Host 'Uninstall check passed.'
exit 0
