param(
  [Parameter(Mandatory = $true, Position = 0)]
  [ValidateSet("waiting", "working", "done", "wait", "work", "finish", "red", "yellow", "green")]
  [string]$State,

  [Parameter(Position = 1)]
  [string]$Message = ""
)

$body = @{
  state = $State
  source = "external"
}

if ($Message.Trim()) {
  $body.message = $Message
}

$json = $body | ConvertTo-Json -Compress
try {
  Invoke-RestMethod `
    -Method Post `
    -Uri "http://127.0.0.1:5058/api/status" `
    -ContentType "application/json; charset=utf-8" `
    -Body $json
} catch {
  $states = @{
    waiting = @{ state = "waiting"; label = "Waiting"; color = "red"; message = "Codex is waiting" }
    wait = @{ state = "waiting"; label = "Waiting"; color = "red"; message = "Codex is waiting" }
    red = @{ state = "waiting"; label = "Waiting"; color = "red"; message = "Codex is waiting" }
    working = @{ state = "working"; label = "Working"; color = "yellow"; message = "Codex appears to be working" }
    work = @{ state = "working"; label = "Working"; color = "yellow"; message = "Codex appears to be working" }
    yellow = @{ state = "working"; label = "Working"; color = "yellow"; message = "Codex appears to be working" }
    done = @{ state = "done"; label = "Done"; color = "green"; message = "Codex work just finished" }
    finish = @{ state = "done"; label = "Done"; color = "green"; message = "Codex work just finished" }
    green = @{ state = "done"; label = "Done"; color = "green"; message = "Codex work just finished" }
  }

  $status = $states[$State]
  if ($Message.Trim()) {
    $status.message = $Message
  }
  $status.updatedAt = (Get-Date).ToUniversalTime().ToString("o")
  $status.source = "external"
  $status | ConvertTo-Json | Set-Content -Encoding UTF8 -Path (Join-Path $PSScriptRoot "status.json")
  $status
}
