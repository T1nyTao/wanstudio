#requires -Version 5.1
<#
.SYNOPSIS
    Capture a Win32/WPF window (or the whole screen) to a PNG, for QA evidence.

.DESCRIPTION
    PrintWindow with PW_RENDERFULLCONTENT (2) is tried first: it works even when the
    window is partly covered or on another virtual desktop, which CopyFromScreen cannot do.
    If PrintWindow fails or returns a uniform (blank) bitmap - a known problem with some
    WPF/DirectComposition surfaces - the script falls back to CopyFromScreen over the
    window rectangle. The capture mode actually used is printed so evidence is honest.

    The window is brought to the foreground first (with the AttachThreadInput dance,
    because a plain SetForegroundWindow from a background process is ignored).

.PARAMETER ProcessName
    Process name without .exe. Defaults to WanStudio.

.PARAMETER OutFile
    Output PNG path, relative to the current directory or absolute. Defaults to qa\shot.png.

.PARAMETER FullScreen
    Ignore the window rect and capture every monitor (virtual screen) with CopyFromScreen.
    Useful when a popup/dialog lives outside the main window bounds.

.PARAMETER DelayMs
    Wait after foregrounding before capturing, so the compositor has settled. Default 400.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\capture-window.ps1 -ProcessName WanStudio -OutFile qa\shot.png

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\capture-window.ps1 -FullScreen -OutFile qa\desktop.png
#>
[CmdletBinding()]
param(
    [string]$ProcessName = 'WanStudio',
    [string]$OutFile = 'qa\shot.png',
    [switch]$FullScreen,
    [int]$DelayMs = 400
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not ('QaWin32' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class QaWin32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    public const int SW_RESTORE = 9;
    public const uint PW_RENDERFULLCONTENT = 2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
}
'@
}

function Write-Step([string]$Message) {
    Write-Host "[capture] $Message"
}

# --- locate the window -------------------------------------------------------
$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
if (-not $proc) {
    Write-Host "[capture] ERROR no top-level window found for process '$ProcessName'." -ForegroundColor Red
    Write-Host "[capture] hint: is the app running unpackaged as WanStudio.exe?"
    exit 1
}
$hwnd = $proc.MainWindowHandle
Write-Step ("process={0} pid={1} hwnd=0x{2:X} title='{3}'" -f $proc.ProcessName, $proc.Id, $hwnd.ToInt64(), $proc.MainWindowTitle)

# --- bring to foreground ----------------------------------------------------
if ([QaWin32]::IsIconic($hwnd)) {
    [void][QaWin32]::ShowWindow($hwnd, [QaWin32]::SW_RESTORE)
    Start-Sleep -Milliseconds 300
}

$fg = [QaWin32]::GetForegroundWindow()
if ($fg -ne $hwnd) {
    $fgPid = 0
    $myPid = $proc.Id
    $fgThread = [QaWin32]::GetWindowThreadProcessId($fg, [ref]$fgPid)
    $targetThread = [QaWin32]::GetWindowThreadProcessId($hwnd, [ref]$fgPid)
    $attached = $false
    if ($fgThread -ne 0 -and $targetThread -ne 0 -and $fgThread -ne $targetThread) {
        $attached = [QaWin32]::AttachThreadInput($fgThread, $targetThread, $true)
    }
    [void][QaWin32]::BringWindowToTop($hwnd)
    [void][QaWin32]::SetForegroundWindow($hwnd)
    if ($attached) {
        [void][QaWin32]::AttachThreadInput($fgThread, $targetThread, $false)
    }
    Start-Sleep -Milliseconds 150
}
Start-Sleep -Milliseconds $DelayMs
$fgOk = ([QaWin32]::GetForegroundWindow() -eq $hwnd)
Write-Step ("foreground={0}" -f $fgOk)

# --- geometry ---------------------------------------------------------------
# DWMWA_EXTENDED_FRAME_BOUNDS excludes the invisible resize border, so crops line up
# with what the user sees; GetWindowRect is the fallback (and includes the shadow).
$rect = New-Object QaWin32+RECT
$dwmOk = $false
try {
    $hr = [QaWin32]::DwmGetWindowAttribute($hwnd, [QaWin32]::DWMWA_EXTENDED_FRAME_BOUNDS, [ref]$rect, 16)
    $dwmOk = ($hr -eq 0 -and $rect.Right -gt $rect.Left -and $rect.Bottom -gt $rect.Top)
} catch {
    $dwmOk = $false
}
if (-not $dwmOk) {
    [void][QaWin32]::GetWindowRect($hwnd, [ref]$rect)
    Write-Step "geometry=GetWindowRect"
} else {
    Write-Step "geometry=DwmGetWindowAttribute(extended frame bounds)"
}

$winX = $rect.Left
$winY = $rect.Top
$winW = $rect.Right - $rect.Left
$winH = $rect.Bottom - $rect.Top

if ($FullScreen) {
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $winX = $vs.X; $winY = $vs.Y; $winW = $vs.Width; $winH = $vs.Height
    Write-Step ("target=virtual-screen x={0} y={1} w={2} h={3}" -f $winX, $winY, $winW, $winH)
} else {
    Write-Step ("target=window x={0} y={1} w={2} h={3}" -f $winX, $winY, $winW, $winH)
    if ($winW -le 0 -or $winH -le 0) {
        Write-Host "[capture] ERROR window rect is empty (minimized or hidden?)." -ForegroundColor Red
        exit 1
    }
}

function Test-BlankBitmap([System.Drawing.Bitmap]$Bmp) {
    # Sample a grid; PrintWindow on DirectComposition surfaces often yields one flat color.
    $first = $null
    $gridX = 12
    $gridY = 12
    for ($iy = 0; $iy -lt $gridY; $iy++) {
        for ($ix = 0; $ix -lt $gridX; $ix++) {
            $px = [int]([math]::Min($Bmp.Width - 1, [math]::Floor($Bmp.Width * ($ix + 0.5) / $gridX)))
            $py = [int]([math]::Min($Bmp.Height - 1, [math]::Floor($Bmp.Height * ($iy + 0.5) / $gridY)))
            $c = $Bmp.GetPixel($px, $py).ToArgb()
            if ($null -eq $first) {
                $first = $c
            } elseif ($c -ne $first) {
                return $false
            }
        }
    }
    return $true
}

# --- attempt 1: PrintWindow -------------------------------------------------
$bmp = $null
$mode = ''
if (-not $FullScreen) {
    $candidate = New-Object System.Drawing.Bitmap($winW, $winH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($candidate)
    $hdc = $g.GetHdc()
    $printed = $false
    try {
        $printed = [QaWin32]::PrintWindow($hwnd, $hdc, [QaWin32]::PW_RENDERFULLCONTENT)
    } finally {
        $g.ReleaseHdc($hdc)
        $g.Dispose()
    }

    if ($printed) {
        # A printed-but-blank bitmap is worse than an honest screenshot, so check it.
        if (Test-BlankBitmap $candidate) {
            Write-Step "PrintWindow(2) returned a blank bitmap -> falling back"
            $candidate.Dispose()
        } else {
            $bmp = $candidate
            $mode = 'PrintWindow(PW_RENDERFULLCONTENT=2)'
        }
    } else {
        Write-Step ("PrintWindow(2) failed (lastError={0}) -> falling back" -f [System.Runtime.InteropServices.Marshal]::GetLastWin32Error())
        $candidate.Dispose()
    }
}

# --- attempt 2: CopyFromScreen ---------------------------------------------
if ($null -eq $bmp) {
    $bmp = New-Object System.Drawing.Bitmap($winW, $winH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen($winX, $winY, 0, 0, (New-Object System.Drawing.Size($winW, $winH)), [System.Drawing.CopyPixelOperation]::SourceCopy)
    } finally {
        $g.Dispose()
    }
    $mode = 'CopyFromScreen'
}

# --- save + report ----------------------------------------------------------
$full = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutFile))
$dir = [System.IO.Path]::GetDirectoryName($full)
if ($dir -and -not (Test-Path -LiteralPath $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$w = $bmp.Width
$h = $bmp.Height
$bmp.Dispose()

$bytes = (Get-Item -LiteralPath $full).Length
Write-Step ("mode={0}" -f $mode)
Write-Step ("image={0}x{1} bytes={2}" -f $w, $h, $bytes)
Write-Step ("file={0}" -f $full)

if ($w -lt 100 -or $h -lt 100) {
    Write-Host "[capture] WARN image is suspiciously small." -ForegroundColor Yellow
}
if ($bytes -lt 3000) {
    Write-Host "[capture] WARN png is very small; likely a blank window." -ForegroundColor Yellow
}
