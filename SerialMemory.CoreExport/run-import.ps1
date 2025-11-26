$env:POSTGRES_HOST='localhost'
$env:POSTGRES_PORT='5435'
$env:POSTGRES_USER='postgres'
$env:POSTGRES_PASSWORD='postgres'
$env:POSTGRES_DB='contextdb'
$env:ONNX_MODEL_PATH='E:\models\model.onnx'
$env:VOCAB_PATH='E:\models\vocab.txt'

Write-Host "Environment variables set:"
Write-Host "POSTGRES_HOST: $env:POSTGRES_HOST"
Write-Host "POSTGRES_PORT: $env:POSTGRES_PORT"
Write-Host "ONNX_MODEL_PATH: $env:ONNX_MODEL_PATH"
Write-Host "VOCAB_PATH: $env:VOCAB_PATH"
Write-Host ""
Write-Host "Starting CORE export and import..."
Write-Host ""

dotnet run all
