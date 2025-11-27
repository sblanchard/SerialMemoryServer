$env:POSTGRES_HOST = "localhost"
$env:POSTGRES_PORT = "5435"
$env:POSTGRES_USER = "postgres"
$env:POSTGRES_PASSWORD = "postgres"
$env:POSTGRES_DB = "contextdb"
$env:SERIALMEMORY_API_KEY = $env:SERIALMEMORY_API_KEY  # Set this environment variable before running

Write-Output '{"jsonrpc":"2.0","id":1,"method":"initialize"}' | & "D:\DEV\SerialMemoryServer\publish\mcp\SerialMemory.Mcp.exe"
