# AIUsageWidget.exe 빌드 + 배포용 zip 생성
# Windows 내장 .NET Framework 4.x 컴파일러를 사용하므로 별도 설치가 필요 없다.
# 실행: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
# 결과: dist\AIUsageWidget.exe, dist\AIUsageWidget-v<버전>.zip (exe + 사용설명서)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

# 1) 아이콘 생성
Add-Type -AssemblyName System.Drawing
$ico = Join-Path $src 'app.ico'
$bmp = New-Object Drawing.Bitmap 64, 64
$g = [Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'
$g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(76, 175, 120))), 2, 2, 60, 60)
$sf = New-Object Drawing.StringFormat
$sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
$font = New-Object Drawing.Font('Segoe UI', 28, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
$g.DrawString('AI', $font, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF(0, 2, 64, 64)), $sf)
$g.Dispose()
$icon = [Drawing.Icon]::FromHandle($bmp.GetHicon())
$fs = [IO.File]::Create($ico); $icon.Save($fs); $fs.Close()

# 2) 컴파일
$cs  = Join-Path $src 'AIUsageWidget.cs'
$exe = Join-Path $dist 'AIUsageWidget.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$exe" "/win32icon:$ico" `
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll $cs
if ($LASTEXITCODE -ne 0) { throw "컴파일 실패 ($LASTEXITCODE)" }

# 3) 배포용 zip (버전은 소스의 AssemblyVersion에서 가져옴)
$m = [regex]::Match([IO.File]::ReadAllText($cs), 'AssemblyVersion\("(\d+\.\d+\.\d+)')
$ver = $m.Groups[1].Value
$stage = Join-Path $dist 'package\AIUsageWidget'
if (Test-Path $stage) { Remove-Item -LiteralPath (Split-Path $stage) -Recurse }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $exe $stage
# 메모장에서 한글이 깨지지 않게 BOM 포함 UTF-8로 넣는다
$guide = [IO.File]::ReadAllText((Join-Path $root 'docs\사용설명서.txt'), [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $stage '사용설명서.txt'), $guide, (New-Object Text.UTF8Encoding($true)))
$zip = Join-Path $dist "AIUsageWidget-v$ver.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, 'Optimal', $true)
Remove-Item -LiteralPath (Split-Path $stage) -Recurse

Get-Item $exe, $zip | Select-Object Name, Length
"SHA256 (exe): " + (Get-FileHash $exe).Hash
