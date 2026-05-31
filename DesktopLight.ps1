param(
  [string]$TargetProcessName = "Codex",
  [int]$Gap = 12,
  [double]$BusyPercent = 2.0,
  [int]$DoneHoldSeconds = 75,
  [switch]$Manual,
  [switch]$NoSnap,
  [switch]$SelfTest
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if (-not ("StatusLight.Win32" -as [type])) {
  Add-Type @"
using System;
using System.Runtime.InteropServices;

namespace StatusLight {
  public struct RECT {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
  }

  public static class Win32 {
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
  }
}
"@
}

$script:StatusPath = Join-Path $PSScriptRoot "status.json"
$script:CurrentState = "waiting"
$script:CurrentMessage = "Codex is waiting"
$script:CurrentLabel = "Waiting"
$script:LastCpuSample = $null
$script:LastBusyAt = [datetime]::MinValue
$script:HasSeenBusy = $false
$script:AutoDetect = -not $Manual
$script:SnapToCodex = -not $NoSnap
$script:Dragging = $false
$script:DragStartMouse = $null
$script:DragStartWindow = $null

function Normalize-State {
  param([string]$State)

  switch -Regex ($State.ToLowerInvariant()) {
    "^(working|work|busy|yellow)$" { "working"; break }
    "^(done|complete|completed|finish|finished|green)$" { "done"; break }
    default { "waiting" }
  }
}

function Get-StateInfo {
  param(
    [string]$State,
    [string]$Message
  )

  $stateName = Normalize-State $State
  $defaults = @{
    waiting = @{ label = "Waiting"; color = "red"; message = "Codex is waiting" }
    working = @{ label = "Working"; color = "yellow"; message = "Codex appears to be working" }
    done = @{ label = "Done"; color = "green"; message = "Codex work just finished" }
  }

  [pscustomobject]@{
    state = $stateName
    label = $defaults[$stateName].label
    color = $defaults[$stateName].color
    message = if ($Message) { $Message } else { $defaults[$stateName].message }
    updatedAt = (Get-Date).ToUniversalTime().ToString("o")
  }
}

function Write-Status {
  param(
    [string]$State,
    [string]$Message
  )

  $next = Get-StateInfo -State $State -Message $Message
  if ($next.state -eq $script:CurrentState -and $next.message -eq $script:CurrentMessage) {
    return
  }

  $script:CurrentState = $next.state
  $script:CurrentLabel = $next.label
  $script:CurrentMessage = $next.message
  $next | ConvertTo-Json | Set-Content -Encoding UTF8 -Path $script:StatusPath
}

function Read-Status {
  if (-not (Test-Path $script:StatusPath)) {
    Write-Status -State "waiting" -Message "Codex is waiting"
  }

  try {
    $status = Get-Content -Raw -Encoding UTF8 -Path $script:StatusPath | ConvertFrom-Json
    $info = Get-StateInfo -State $status.state -Message $status.message
    $script:CurrentState = $info.state
    $script:CurrentLabel = $info.label
    $script:CurrentMessage = $info.message
  } catch {
    $script:CurrentState = "waiting"
    $script:CurrentLabel = "Waiting"
    $script:CurrentMessage = "Codex is waiting"
  }
}

function Get-CodexWindow {
  Get-Process -ErrorAction SilentlyContinue |
    Where-Object {
      $_.ProcessName -ieq $TargetProcessName -and
      $_.MainWindowHandle -ne 0 -and
      [StatusLight.Win32]::IsWindowVisible($_.MainWindowHandle)
    } |
    Sort-Object @{ Expression = { if ($_.MainWindowTitle -eq "Codex") { 0 } else { 1 } } }, Id |
    Select-Object -First 1
}

function Get-CodexCpuPercent {
  $now = Get-Date
  $cpu = 0.0

  Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -ieq "Codex" -or $_.ProcessName -ieq "codex" } |
    ForEach-Object {
      if ($null -ne $_.CPU) {
        $cpu += [double]$_.CPU
      }
    }

  if ($null -eq $script:LastCpuSample) {
    $script:LastCpuSample = [pscustomobject]@{ Time = $now; Cpu = $cpu }
    return 0.0
  }

  $elapsed = ($now - $script:LastCpuSample.Time).TotalSeconds
  if ($elapsed -le 0) {
    return 0.0
  }

  $delta = [Math]::Max(0.0, $cpu - $script:LastCpuSample.Cpu)
  $script:LastCpuSample = [pscustomobject]@{ Time = $now; Cpu = $cpu }
  return ($delta / $elapsed / [Environment]::ProcessorCount) * 100.0
}

function Update-AutoStatus {
  if (-not $script:AutoDetect) {
    return
  }

  $percent = Get-CodexCpuPercent
  $now = Get-Date

  if ($percent -ge $BusyPercent) {
    $script:LastBusyAt = $now
    $script:HasSeenBusy = $true
    Write-Status -State "working" -Message ("Codex is working, CPU {0:N1}%" -f $percent)
    return
  }

  if ($script:HasSeenBusy -and ($now - $script:LastBusyAt).TotalSeconds -lt $DoneHoldSeconds) {
    Write-Status -State "done" -Message "Codex work just finished"
    return
  }

  $script:HasSeenBusy = $false
  Write-Status -State "waiting" -Message "Codex is waiting"
}

function New-RoundPath {
  param(
    [System.Drawing.RectangleF]$Rect,
    [single]$Radius
  )

  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $diameter = $Radius * 2
  $path.AddArc($Rect.X, $Rect.Y, $diameter, $diameter, 180, 90)
  $path.AddArc($Rect.Right - $diameter, $Rect.Y, $diameter, $diameter, 270, 90)
  $path.AddArc($Rect.Right - $diameter, $Rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
  $path.AddArc($Rect.X, $Rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
  $path.CloseFigure()
  return $path
}

function Draw-Light {
  param(
    [System.Drawing.Graphics]$Graphics,
    [int]$X,
    [int]$Y,
    [int]$Size,
    [System.Drawing.Color]$Color,
    [bool]$Active
  )

  $outer = New-Object System.Drawing.Rectangle ($X, $Y, $Size, $Size)
  $rimBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 6, 8, 9))
  $Graphics.FillEllipse($rimBrush, $outer)
  $rimBrush.Dispose()

  if ($Active) {
    for ($i = 3; $i -ge 1; $i--) {
      $grow = 8 * $i
      $alpha = 28 + (18 * (4 - $i))
      $glowRect = New-Object System.Drawing.Rectangle ($X - $grow, $Y - $grow, $Size + $grow * 2, $Size + $grow * 2)
      $glowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($alpha, $Color))
      $Graphics.FillEllipse($glowBrush, $glowRect)
      $glowBrush.Dispose()
    }
  }

  $inner = New-Object System.Drawing.Rectangle ($X + 8, $Y + 8, $Size - 16, $Size - 16)
  $baseColor = if ($Active) { $Color } else { [System.Drawing.Color]::FromArgb(255, 38, 43, 45) }
  $lensBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ($inner, [System.Drawing.Color]::FromArgb(255, $baseColor), [System.Drawing.Color]::FromArgb(255, 13, 15, 16), 65)
  $Graphics.FillEllipse($lensBrush, $inner)
  $lensBrush.Dispose()

  $shine = New-Object System.Drawing.Rectangle ($X + 19, $Y + 17, 16, 10)
  $shineAlpha = if ($Active) { 155 } else { 30 }
  $shineBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($shineAlpha, 255, 255, 255))
  $Graphics.FillEllipse($shineBrush, $shine)
  $shineBrush.Dispose()
}

function Update-Position {
  param([System.Windows.Forms.Form]$Form)

  if (-not $script:SnapToCodex) {
    return
  }

  $target = Get-CodexWindow
  if (-not $target) {
    return
  }

  $rect = New-Object StatusLight.RECT
  if (-not [StatusLight.Win32]::GetWindowRect($target.MainWindowHandle, [ref]$rect)) {
    return
  }

  $screen = [System.Windows.Forms.Screen]::FromHandle($target.MainWindowHandle).WorkingArea
  $x = $rect.Right + $Gap
  if ($x + $Form.Width -gt $screen.Right) {
    $x = $rect.Left - $Gap - $Form.Width
  }

  $y = $rect.Top + 72
  if ($y + $Form.Height -gt $screen.Bottom) {
    $y = $screen.Bottom - $Form.Height - 8
  }
  if ($y -lt $screen.Top) {
    $y = $screen.Top + 8
  }

  $Form.SetDesktopLocation([int]$x, [int]$y)
}

if ($SelfTest) {
  Read-Status
  $target = Get-CodexWindow
  $cpuPercent = Get-CodexCpuPercent
  [pscustomobject]@{
    targetFound = $null -ne $target
    targetProcessId = if ($target) { $target.Id } else { $null }
    targetTitle = if ($target) { $target.MainWindowTitle } else { $null }
    status = $script:CurrentState
    autoDetect = $script:AutoDetect
    snapToCodex = $script:SnapToCodex
    cpuPercent = [Math]::Round($cpuPercent, 2)
  } | ConvertTo-Json -Compress
  exit
}

[System.Windows.Forms.Application]::EnableVisualStyles()
Read-Status

$form = New-Object System.Windows.Forms.Form
$form.Text = "Codex Work Status Light"
$form.Width = 84
$form.Height = 214
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.ShowInTaskbar = $false
$form.TopMost = $true
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.BackColor = [System.Drawing.Color]::FromArgb(23, 25, 27)
$form.Padding = New-Object System.Windows.Forms.Padding 0

$panel = New-Object System.Windows.Forms.Panel
$panel.Dock = [System.Windows.Forms.DockStyle]::Fill
$panel.BackColor = [System.Drawing.Color]::Transparent
$form.Controls.Add($panel)

$tip = New-Object System.Windows.Forms.ToolTip
$tip.SetToolTip($panel, "Codex Work Status Light")

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$autoItem = New-Object System.Windows.Forms.ToolStripMenuItem "Auto detect"
$autoItem.CheckOnClick = $true
$autoItem.Checked = $script:AutoDetect
$snapItem = New-Object System.Windows.Forms.ToolStripMenuItem "Snap to Codex"
$snapItem.CheckOnClick = $true
$snapItem.Checked = $script:SnapToCodex
$waitItem = New-Object System.Windows.Forms.ToolStripMenuItem "Red: waiting"
$workItem = New-Object System.Windows.Forms.ToolStripMenuItem "Yellow: working"
$doneItem = New-Object System.Windows.Forms.ToolStripMenuItem "Green: done"
$exitItem = New-Object System.Windows.Forms.ToolStripMenuItem "Exit"
[void]$menu.Items.Add($autoItem)
[void]$menu.Items.Add($snapItem)
[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$menu.Items.Add($waitItem)
[void]$menu.Items.Add($workItem)
[void]$menu.Items.Add($doneItem)
[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$menu.Items.Add($exitItem)
$panel.ContextMenuStrip = $menu
$form.ContextMenuStrip = $menu

$autoItem.Add_CheckedChanged({
  $script:AutoDetect = $autoItem.Checked
})

$snapItem.Add_CheckedChanged({
  $script:SnapToCodex = $snapItem.Checked
})

$waitItem.Add_Click({
  $script:AutoDetect = $false
  $autoItem.Checked = $false
  Write-Status -State "waiting" -Message "Manually set to waiting"
  $panel.Invalidate()
})

$workItem.Add_Click({
  $script:AutoDetect = $false
  $autoItem.Checked = $false
  Write-Status -State "working" -Message "Manually set to working"
  $panel.Invalidate()
})

$doneItem.Add_Click({
  $script:AutoDetect = $false
  $autoItem.Checked = $false
  Write-Status -State "done" -Message "Manually set to done"
  $panel.Invalidate()
})

$exitItem.Add_Click({ $form.Close() })

$panel.Add_Paint({
  param($sender, $event)

  $g = $event.Graphics
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

  $body = New-Object System.Drawing.RectangleF 7, 7, 70, 198
  $bodyPath = New-RoundPath -Rect $body -Radius 18
  $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ($body, [System.Drawing.Color]::FromArgb(255, 48, 52, 55), [System.Drawing.Color]::FromArgb(255, 12, 14, 15), 90)
  $g.FillPath($bodyBrush, $bodyPath)
  $bodyBrush.Dispose()
  $borderPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 6, 8, 9)), 2
  $g.DrawPath($borderPen, $bodyPath)
  $borderPen.Dispose()
  $bodyPath.Dispose()

  Draw-Light -Graphics $g -X 19 -Y 20 -Size 46 -Color ([System.Drawing.Color]::FromArgb(235, 53, 70)) -Active ($script:CurrentState -eq "waiting")
  Draw-Light -Graphics $g -X 19 -Y 83 -Size 46 -Color ([System.Drawing.Color]::FromArgb(255, 200, 69)) -Active ($script:CurrentState -eq "working")
  Draw-Light -Graphics $g -X 19 -Y 146 -Size 46 -Color ([System.Drawing.Color]::FromArgb(32, 195, 106)) -Active ($script:CurrentState -eq "done")
})

$panel.Add_MouseDown({
  param($sender, $event)
  if ($event.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
    $script:Dragging = $true
    $script:SnapToCodex = $false
    $snapItem.Checked = $false
    $script:DragStartMouse = [System.Windows.Forms.Cursor]::Position
    $script:DragStartWindow = $form.Location
  }
})

$panel.Add_MouseMove({
  if ($script:Dragging) {
    $mouse = [System.Windows.Forms.Cursor]::Position
    $dx = $mouse.X - $script:DragStartMouse.X
    $dy = $mouse.Y - $script:DragStartMouse.Y
    $form.Location = New-Object System.Drawing.Point (($script:DragStartWindow.X + $dx), ($script:DragStartWindow.Y + $dy))
  }
})

$panel.Add_MouseUp({
  param($sender, $event)
  if ($event.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
    $script:Dragging = $false
  }
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 800
$timer.Add_Tick({
  Update-AutoStatus
  Read-Status
  Update-Position -Form $form
  $tip.SetToolTip($panel, "$script:CurrentLabel - $script:CurrentMessage")
  $panel.Invalidate()
})
$timer.Start()

$form.Add_Shown({ Update-Position -Form $form })
[System.Windows.Forms.Application]::Run($form)
