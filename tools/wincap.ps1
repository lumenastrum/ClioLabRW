# Capture the lab RimWorld window (by lab.pid) to a PNG — what a human would see, UI and all.
#   pwsh tools\wincap.ps1 -Out C:\path\shot.png
param([Parameter(Mandatory)][string]$Out)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W { [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r); }
"@
$labData = if ($env:RW_LAB_DATA) { $env:RW_LAB_DATA } else { Join-Path $HOME 'RWLab' }
$labPid = [int](Get-Content (Join-Path $labData 'lab.pid'))
$h = (Get-Process -Id $labPid).MainWindowHandle
$r = New-Object W+R
[void][W]::GetWindowRect($h, [ref]$r)
$w = $r.Ri - $r.L; $ht = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"$Out ${w}x$ht"
