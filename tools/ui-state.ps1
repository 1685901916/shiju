param([string]$AppName = 'SelectionAssistant', [string]$WindowId = '')
$arguments = @('snapshot', '--app', $AppName, '--compact', '--timeout-ms', '7000')
if ($WindowId) { $arguments += @('--window-id', $WindowId) }
$snapshot = (& agent-desktop @arguments) | ConvertFrom-Json
if (-not $snapshot.ok) { $snapshot | ConvertTo-Json -Depth 12 -Compress; exit 1 }
function Read-Node($node) {
    if ($node.ref_id -and $node.role -ne 'group' -and $node.role -ne 'scrollbar') {
        [pscustomobject]@{ ref=$node.ref_id; role=$node.role; name=$node.name; value=$node.value; states=$node.states } | ConvertTo-Json -Compress -Depth 4
    }
    if ($node.role -eq 'statictext' -and $node.name) { Write-Output $node.name }
    foreach ($child in $node.children) { Read-Node $child }
}
Read-Node $snapshot.data.tree
