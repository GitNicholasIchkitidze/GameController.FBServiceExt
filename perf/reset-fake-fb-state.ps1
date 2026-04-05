[CmdletBinding()]
param(
    [string]$SqlContainerName = 'fbserviceext-sqlserver',
    [string]$SqlDatabaseName = 'GameControllerFBServiceExt',
    [string]$SqlSaPassword = 'FbServiceExt_Strong_2026!',
    [string]$RedisContainerName = 'fbserviceext-redis',
    [string]$RedisKeyPrefix = 'fbserviceext'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host 'Resetting SQL tables...' -ForegroundColor DarkCyan
& docker exec $SqlContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $SqlSaPassword -C -d $SqlDatabaseName -Q "DELETE FROM dbo.AcceptedVotes; DELETE FROM dbo.NormalizedEvents;"
if ($LASTEXITCODE -ne 0) {
    throw 'SQL reset failed.'
}

$lua = @"
local cursor = '0'
local total = 0
repeat
  local result = redis.call('SCAN', cursor, 'MATCH', ARGV[1], 'COUNT', 1000)
  cursor = result[1]
  local keys = result[2]
  local count = #keys
  if count > 0 then
    for startIndex = 1, count, 500 do
      local batch = {}
      local endIndex = math.min(startIndex + 499, count)
      for keyIndex = startIndex, endIndex do
        batch[#batch + 1] = keys[keyIndex]
      end
      redis.call('UNLINK', unpack(batch))
      total = total + #batch
    end
  end
until cursor == '0'
return total
"@

Write-Host 'Cleaning Redis prefix state...' -ForegroundColor DarkCyan
$deletedCount = & docker exec $RedisContainerName redis-cli --raw EVAL $lua 0 "${RedisKeyPrefix}:*"
if ($LASTEXITCODE -ne 0) {
    throw 'Redis cleanup failed.'
}

Write-Host ("Redis keys removed: {0}" -f ($deletedCount | Select-Object -Last 1)) -ForegroundColor DarkCyan
