#requires -Version 5.1
<#
.SYNOPSIS
  Walks a process' virtual address space (VirtualQueryEx) and summarises committed
  memory by region type, so we can separate managed heap from native/image/stack memory.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/memory/mem-regions.ps1 -ProcessName carton
#>
param(
    [string]$ProcessName = 'carton',
    [int]$ProcessId = 0
)

$ErrorActionPreference = 'Stop'

if (-not ([System.Management.Automation.PSTypeName]'MemWalker').Type) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MemWalker
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualQueryEx(IntPtr proc, IntPtr addr,
        out MEMORY_BASIC_INFORMATION info, IntPtr len);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetMappedFileNameW(IntPtr proc, IntPtr addr,
        System.Text.StringBuilder name, uint size);
}
'@
}

if ($ProcessId -gt 0) {
    $proc = Get-Process -Id $ProcessId
} else {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $proc) { Write-Error "process not found"; exit 1 }

# PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
$h = [MemWalker]::OpenProcess(0x0400 -bor 0x0010, $false, $proc.Id)
if ($h -eq [IntPtr]::Zero) { Write-Error "OpenProcess failed (try elevated)"; exit 1 }

$MEM_COMMIT = 0x1000
$MEM_IMAGE = 0x1000000
$MEM_MAPPED = 0x40000
$MEM_PRIVATE = 0x20000

$size = [System.Runtime.InteropServices.Marshal]::SizeOf([type][MemWalker+MEMORY_BASIC_INFORMATION])
$addr = [IntPtr]::Zero
$max = [IntPtr]::new(0x7FFFFFFF0000)

$buckets = @{}
$privateRegions = New-Object System.Collections.Generic.List[object]
$total = 0L

while ($addr.ToInt64() -lt $max.ToInt64()) {
    $mbi = New-Object MemWalker+MEMORY_BASIC_INFORMATION
    $r = [MemWalker]::VirtualQueryEx($h, $addr, [ref]$mbi, [IntPtr]$size)
    if ($r -eq [IntPtr]::Zero) { break }

    $rsize = $mbi.RegionSize.ToInt64()
    if ($rsize -le 0) { break }

    if ($mbi.State -eq $MEM_COMMIT) {
        $total += $rsize
        $kind = switch ($mbi.Type) {
            $MEM_IMAGE   { 'Image (DLL/EXE)' }
            $MEM_MAPPED  { 'Mapped (file/shared)' }
            $MEM_PRIVATE { 'Private (heap/stack/JIT)' }
            default      { "Other($($mbi.Type))" }
        }
        if (-not $buckets.ContainsKey($kind)) { $buckets[$kind] = 0L }
        $buckets[$kind] += $rsize

        if ($mbi.Type -eq $MEM_PRIVATE -and $rsize -ge 1MB) {
            $privateRegions.Add([pscustomobject]@{
                Base = '0x{0:X}' -f $mbi.BaseAddress.ToInt64()
                MB   = [math]::Round($rsize / 1MB, 2)
                Protect = '0x{0:X}' -f $mbi.Protect
            })
        }
    }

    $next = $mbi.BaseAddress.ToInt64() + $rsize
    if ($next -le $addr.ToInt64()) { break }
    $addr = [IntPtr]::new($next)
}

[void][MemWalker]::CloseHandle($h)

Write-Output "PID=$($proc.Id)  WorkingSet=$([math]::Round($proc.WorkingSet64/1MB,1))MB  Private=$([math]::Round($proc.PrivateMemorySize64/1MB,1))MB  Threads=$($proc.Threads.Count)"
Write-Output ("Total committed: {0} MB" -f [math]::Round($total/1MB, 1))
Write-Output ''
Write-Output '--- committed by region type ---'
$buckets.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
    '{0,-28} {1,8} MB' -f $_.Key, [math]::Round($_.Value/1MB, 1)
}
Write-Output ''
Write-Output '--- largest private regions (>=1MB) ---'
$privateRegions | Sort-Object MB -Descending | Select-Object -First 25 | Format-Table -AutoSize | Out-String -Width 120

Write-Output '--- top native modules by image size ---'
$proc.Modules | Sort-Object ModuleMemorySize -Descending | Select-Object -First 20 `
    @{n='Module';e={$_.ModuleName}}, @{n='MB';e={[math]::Round($_.ModuleMemorySize/1MB,2)}} |
    Format-Table -AutoSize | Out-String -Width 120
