<#
  WanStudio UIA driver (QA evidence only; not part of the product build).

  Why ASCII-only: this file is launched by Windows PowerShell 5.1, which reads
  BOM-less script files as ANSI/GBK. Non-ASCII literals break parsing. So all
  strings here are ASCII, and any Chinese text is passed via -TextFile (UTF-8).

  Usage:
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/uia/drive.ps1 -Action state
    ... -Action dump
    ... -Action checkboxes -Scope SegmentList
    ... -Action toggle -Scope SegmentList -Index 0
    ... -Action buttons
    ... -Action invoke -Id GenerateSelectedButton
    ... -Action invokeidx -Index 5
    ... -Action settext -Id BatchPromptBox -TextFile qa/_tmp/prompt.txt
    ... -Action combosel -Id SplitModeCombo -Index 1
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Action,

    [string]$Id,
    [string]$Scope,
    [int]$Index = -1,
    [string]$Text,
    [string]$TextFile,
    [int]$WaitMs = 700,
    [switch]$Patterns
)

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$TrueCond = [System.Windows.Automation.Condition]::TrueCondition

function Get-Root {
    $p = Get-Process -Name WanStudio -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { throw "NO_PROCESS" }
    if ($p.MainWindowHandle -eq [IntPtr]::Zero) { throw "NO_WINDOW" }
    $root = $AE::FromHandle($p.MainWindowHandle)
    if (-not $root) { throw "NO_UIA_ROOT" }
    return $root
}

function Find-ById([string]$eid) {
    $root = Get-Root
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $eid)
    $el = $root.FindFirst($TS::Descendants, $cond)
    if (-not $el) { throw "NOT_FOUND_ID: $eid" }
    return $el
}

function Get-Scope([string]$scopeId) {
    if (-not $scopeId) { return Get-Root }
    return Find-ById $scopeId
}

function Get-Text($el) {
    if (-not $el) { return "" }
    try {
        $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if ($vp) { return [string]$vp.Current.Value }
    } catch { }
    return [string]$el.Current.Name
}

function Do-Invoke($el) {
    try {
        $ip = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        if ($ip) { $ip.Invoke(); return "InvokePattern" }
    } catch { }
    try {
        $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($tp) { $tp.Toggle(); return "TogglePattern" }
    } catch { }
    try {
        $lp = $el.GetCurrentPattern([System.Windows.Automation.LegacyIAccessiblePattern]::Pattern)
        if ($lp) { $lp.DoDefaultAction(); return "LegacyIAccessible" }
    } catch { }
    try {
        $pt = $el.GetClickablePoint()
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]$pt.X, [int]$pt.Y)
        Start-Sleep -Milliseconds 150
        if (-not ("MClick" -as [type])) {
            Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MClick {
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  public const uint DOWN = 0x0002, UP = 0x0004;
  public static void Click() { mouse_event(DOWN,0,0,0,IntPtr.Zero); mouse_event(UP,0,0,0,IntPtr.Zero); }
}
"@
        }
        [MClick]::Click()
        return ("MouseClick@{0},{1}" -f [int]$pt.X, [int]$pt.Y)
    } catch { }
    throw "NO_ACTION_PATTERN"
}

function Get-Buttons([string]$scopeId) {
    $scopeEl = Get-Scope $scopeId
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
    return $scopeEl.FindAll($TS::Descendants, $cond)
}

function Get-CheckBoxes([string]$scopeId) {
    $scopeEl = Get-Scope $scopeId
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::CheckBox)
    return $scopeEl.FindAll($TS::Descendants, $cond)
}

function Toggle-State($box) {
    try { return [string]$box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState }
    catch { return "?" }
}

function Describe($e, [int]$i) {
    $c = $e.Current
    $type = ([string]$c.ControlType.ProgrammaticName).Replace("ControlType.", "")
    $pats = ""
    if ($Patterns) {
        $names = @()
        foreach ($p in $e.GetSupportedPatterns()) { $names += ([string]$p.ProgrammaticName).Replace("PatternIdentifiers.Pattern", "") }
        $pats = " patterns=[" + ($names -join ",") + "]"
    }
    $name = [string]$c.Name
    if ($name.Length -gt 70) { $name = $name.Substring(0, 70) + "..." }
    return "[$i] $type id='$($c.AutomationId)' name='$name' enabled=$($c.IsEnabled) off=$($c.IsOffscreen)$pats"
}

switch ($Action) {

    "dump" {
        $root = Get-Root
        $all = $root.FindAll($TS::Descendants, $TrueCond)
        Write-Output ("count=" + $all.Count)
        $i = 0
        foreach ($e in $all) { Write-Output (Describe $e $i); $i++ }
    }

    "dumpnamed" {
        $root = Get-Root
        $all = $root.FindAll($TS::Descendants, $TrueCond)
        $i = 0
        foreach ($e in $all) {
            if ($e.Current.Name -or $e.Current.AutomationId) { Write-Output (Describe $e $i) }
            $i++
        }
    }

    "checkboxes" {
        $boxes = Get-CheckBoxes $Scope
        Write-Output ("scope='" + $Scope + "' checkbox_count=" + $boxes.Count)
        $i = 0
        foreach ($b in $boxes) {
            Write-Output ("[$i] name='$($b.Current.Name)' id='$($b.Current.AutomationId)' state=$(Toggle-State $b) enabled=$($b.Current.IsEnabled) off=$($b.Current.IsOffscreen)")
            $i++
        }
    }

    "toggle" {
        $boxes = Get-CheckBoxes $Scope
        if ($Index -lt 0 -or $Index -ge $boxes.Count) { throw ("INDEX_OUT_OF_RANGE count=" + $boxes.Count) }
        $b = $boxes[$Index]
        $before = Toggle-State $b
        $how = Do-Invoke $b
        Start-Sleep -Milliseconds $WaitMs
        $after = Toggle-State $b
        Write-Output "toggled[$Index] via=$how before=$before after=$after"
    }

    "toggleall" {
        $boxes = Get-CheckBoxes $Scope
        $i = 0
        foreach ($b in $boxes) {
            $before = Toggle-State $b
            if ($before -ne "On") { Do-Invoke $b | Out-Null; Start-Sleep -Milliseconds 120 }
            $i++
        }
        Write-Output ("toggled_on=" + $i)
    }

    "buttons" {
        $bs = Get-Buttons $Scope
        Write-Output ("button_count=" + $bs.Count)
        $i = 0
        foreach ($b in $bs) {
            Write-Output ("[$i] name='$($b.Current.Name)' id='$($b.Current.AutomationId)' enabled=$($b.Current.IsEnabled) off=$($b.Current.IsOffscreen)")
            $i++
        }
    }

    "invoke" {
        $el = Find-ById $Id
        if (-not $el.Current.IsEnabled) {
            Write-Output ("DISABLED id=" + $Id)
            exit 0
        }
        $how = Do-Invoke $el
        Start-Sleep -Milliseconds $WaitMs
        Write-Output "invoked id=$Id via=$how"
    }

    "press" {
        # Focus the element and fire Space via keyboard. WinUI buttons respond to
        # Space/Enter when focused even when InvokePattern silently does nothing.
        $el = Find-ById $Id
        if (-not $el.Current.IsEnabled) { Write-Output ("DISABLED id=" + $Id); exit 0 }
        try {
            $sp = $el.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)
            if ($sp) { $sp.ScrollIntoView(); Start-Sleep -Milliseconds 400 }
        } catch { }
        $el.SetFocus()
        Start-Sleep -Milliseconds 350
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.SendKeys]::SendWait(" ")
        Start-Sleep -Milliseconds $WaitMs
        Write-Output ("pressed id=" + $Id + " key=Space")
    }

    "rect" {
        $el = Find-ById $Id
        $r = $el.Current.BoundingRectangle
        Write-Output ("id=" + $Id + " rect=" + $r.X + "," + $r.Y + " " + $r.Width + "x" + $r.Height + " offscreen=" + $el.Current.IsOffscreen)
    }

    "click" {
        # Force a real mouse click. WinUI buttons can accept InvokePattern
        # without firing Click, and off-screen elements have no clickable point.
        $el = Find-ById $Id
        if (-not $el.Current.IsEnabled) { Write-Output ("DISABLED id=" + $Id); exit 0 }

        # Bring it into view first (long page lives inside a ScrollViewer).
        try {
            $sp = $el.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)
            if ($sp) { $sp.ScrollIntoView(); Start-Sleep -Milliseconds 450 }
        } catch { }

        $rect = $el.Current.BoundingRectangle
        $pt = $null
        if ($rect.Width -gt 0 -and $rect.Height -gt 0) {
            $pt = New-Object System.Drawing.Point([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
        } else {
            try { $pt = $el.GetClickablePoint() } catch { }
        }
        if (-not $pt) { throw ("NO_CLICK_POINT id=" + $Id + " rect=" + $rect) }

        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        [System.Windows.Forms.Cursor]::Position = $pt
        Start-Sleep -Milliseconds 220
        if (-not ("MClick" -as [type])) {
            Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MClick {
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  public const uint DOWN = 0x0002, UP = 0x0004;
  public static void Click() { mouse_event(DOWN,0,0,0,IntPtr.Zero); mouse_event(UP,0,0,0,IntPtr.Zero); }
}
"@
        }
        [MClick]::Click()
        Start-Sleep -Milliseconds $WaitMs
        Write-Output ("clicked id=" + $Id + " at=" + $pt.X + "," + $pt.Y)
    }

    "invokeidx" {
        $bs = Get-Buttons $Scope
        if ($Index -lt 0 -or $Index -ge $bs.Count) { throw ("INDEX_OUT_OF_RANGE count=" + $bs.Count) }
        $b = $bs[$Index]
        $nm = [string]$b.Current.Name
        if (-not $b.Current.IsEnabled) { Write-Output "DISABLED name='$nm'"; exit 0 }
        $how = Do-Invoke $b
        Start-Sleep -Milliseconds $WaitMs
        Write-Output "invoked[$Index] name='$nm' via=$how"
    }

    "settext" {
        $el = Find-ById $Id
        $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if (-not $vp) { throw "NO_VALUEPATTERN" }
        $val = $Text
        if ($TextFile) { $val = (Get-Content -LiteralPath $TextFile -Encoding UTF8 -Raw).TrimEnd("`r", "`n") }
        $vp.SetValue($val)
        Start-Sleep -Milliseconds $WaitMs
        Write-Output ("settext id=" + $Id + " len=" + $val.Length)
    }

    "gettext" {
        $el = Find-ById $Id
        Write-Output ("text id=" + $Id + " = '" + (Get-Text $el) + "'")
    }

    "combosel" {
        $el = Find-ById $Id
        $ep = $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $ep.Expand()
        Start-Sleep -Milliseconds 450
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)
        $items = $el.FindAll($TS::Descendants, $cond)
        if ($Index -lt 0 -or $Index -ge $items.Count) {
            try { $ep.Collapse() } catch { }
            throw ("ITEM_INDEX_OUT_OF_RANGE count=" + $items.Count)
        }
        $it = $items[$Index]
        $sp = $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sp.Select()
        Start-Sleep -Milliseconds 350
        try { $ep.Collapse() } catch { }
        Write-Output ("combo id=" + $Id + " selectedIndex=" + $Index + " name='" + $it.Current.Name + "'")
    }

    "state" {
        $root = Get-Root
        $ids = @(
            "SegmentHeader", "GenerateSelectedButton", "GenerateHint",
            "PoolHeader", "PoolEmpty",
            "ContinueTarget", "ContinueButton", "ContinueHint", "ContinueModelCombo", "ContinueDurationBox",
            "StitchButton", "StitchHint", "StitchResult", "StitchToggle",
            "SplitResult", "SplitButton", "SplitModeCombo", "SegmentSecondsBox",
            "PreviewTitle", "PreviewHint", "TrimStartBox", "TrimDurationBox",
            "SourceName", "SourceMeta", "EngineText", "BatchPromptBox"
        )
        foreach ($eid in $ids) {
            $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $eid)
            $el = $root.FindFirst($TS::Descendants, $cond)
            if ($el) {
                $v = (Get-Text $el) -replace "`r?`n", " / "
                if ($v.Length -gt 160) { $v = $v.Substring(0, 160) + "..." }
                Write-Output ("$eid | enabled=$($el.Current.IsEnabled) | $v")
            } else {
                Write-Output ("$eid | (absent)")
            }
        }
        foreach ($sid in @("Title", "Message")) {
            $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $sid)
            $el = $root.FindFirst($TS::Descendants, $cond)
            if ($el) {
                $v = (Get-Text $el) -replace "`r?`n", " / "
                Write-Output ("StatusBar.$sid | $v")
            }
        }
    }

    default { throw ("UNKNOWN_ACTION: " + $Action) }
}
