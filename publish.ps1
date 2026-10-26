# Собирает самодостаточные LanChat.exe (64- и 32-бит) в папку dist\.
# .NET на компьютерах участников НЕ нужен — всё внутри exe.
param(
    [ValidateSet('x64', 'x86', 'both')]
    [string]$Arch = 'both'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$proj = Join-Path $root 'src\LanChat\LanChat.csproj'
$out  = Join-Path $root 'dist'

function Publish-One($rid, $suffix) {
    $target = Join-Path $out $rid
    dotnet publish $proj `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -o $target

    $built = Join-Path $target 'LanChat.exe'
    $final = Join-Path $out "LanChat-$suffix.exe"
    if (Test-Path $built) {
        # Сразу после publish антивирус иногда на долю секунды держит файл —
        # пара попыток с паузой решает это без вмешательства человека.
        for ($i = 1; $i -le 5; $i++) {
            try { Copy-Item $built $final -Force -ErrorAction Stop; break }
            catch {
                if ($i -eq 5) { throw }
                Start-Sleep -Milliseconds 500
            }
        }
        $mb = [math]::Round((Get-Item $final).Length / 1MB, 1)
        Write-Host ""
        Write-Host "Готово: $final  ($mb МБ)" -ForegroundColor Green
    }
}

if ($Arch -eq 'x64' -or $Arch -eq 'both') { Publish-One 'win-x64' '64bit' }
if ($Arch -eq 'x86' -or $Arch -eq 'both') { Publish-One 'win-x86' '32bit' }

Write-Host ""
Write-Host "Берите LanChat-64bit.exe для обычных (64-бит) компьютеров и LanChat-32bit.exe для старых 32-битных. Если не знаете, какая у вас — почти наверняка 64-бит." -ForegroundColor Cyan
