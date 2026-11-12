# LanChat MSIX 打包脚本
# 流程：dotnet publish（文件夹模式）→ 组装 AppxManifest 与 Logo → makeappx 打包 → 自签名（可选）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File Packaging\build-msix.ps1
#   powershell -ExecutionPolicy Bypass -File Packaging\build-msix.ps1 -NoSign   # 商店提交用：不签名
#
# 产物：Packaging\out\LanChat_<版本>.msix（及本机测试用 .pfx/.cer）
# makeappx/signtool 优先用本机 Windows SDK，缺失时自动从 NuGet 官方包下载到 Packaging\.tools。
param(
    [switch]$NoSign,
    [string]$CertPassword = "LanChat-Msix"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Find-Tool([string]$name) {
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kits) {
        $hit = Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    # 回退：Microsoft.Windows.SDK.BuildTools NuGet 包（约 22MB，仅含工具）
    $tools = Join-Path $PSScriptRoot ".tools"
    $extract = Join-Path $tools "extract"
    if (-not (Test-Path (Join-Path $extract $name))) {
        $ver = "10.0.28000.2957"
        $nupkg = Join-Path $tools "buildtools.nupkg"
        New-Item -ItemType Directory -Force -Path $tools | Out-Null
        if (-not (Test-Path $nupkg)) {
            Write-Host "下载 Windows SDK BuildTools（$ver）…"
            Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$ver/microsoft.windows.sdk.buildtools.$ver.nupkg" -OutFile $nupkg
        }
        $zip = Join-Path $tools "buildtools.zip"
        Copy-Item $nupkg $zip -Force
        if (-not (Test-Path $extract)) { Expand-Archive $zip -DestinationPath $extract -Force }
        Remove-Item $zip -ErrorAction SilentlyContinue
    }
    $found = Get-ChildItem $extract -Recurse -Filter $name |
        Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
    if (-not $found) { throw "找不到 $name（Windows SDK 与 NuGet 回退均失败）" }
    return $found.FullName
}

# 1) 读取版本号（与 csproj 保持一致，MSIX 要求四段）
$csprojText = Get-Content (Join-Path $root "LanChat.csproj") -Raw
$version = [regex]::Match($csprojText, '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $version) { throw "无法从 LanChat.csproj 解析 <Version>" }
if ($version.Split('.').Count -eq 3) { $version = "$version.0" }
Write-Host "打包版本：$version"

# 2) 停止正在运行的实例，避免文件占用
Get-Process LanChat -ErrorAction SilentlyContinue | Stop-Process -Force

# 3) 文件夹模式发布（MSIX 内以目录布局运行，不用单文件自解压）
$stage = Join-Path $PSScriptRoot "stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Write-Host "dotnet publish → Packaging\stage …"
dotnet publish (Join-Path $root "LanChat.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:SatelliteResourceLanguages=zh-Hans `
    -p:DebugType=None -p:DebugSymbols=false -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$LASTEXITCODE）" }

# 4) 写入清单（替换版本）与 Logo
$manifest = Get-Content (Join-Path $PSScriptRoot "AppxManifest.xml") -Raw -Encoding UTF8
# 区分大小写：只替换 Identity 的 Version=（PS -replace 不分大小写会误伤 xml 声明的 version="1.0"）
$manifest = [regex]::Replace($manifest, '(?<=<Identity[^>]*\s)Version="[\d.]+"', "Version=`"$version`"")
Set-Content -Path (Join-Path $stage "AppxManifest.xml") -Value $manifest -Encoding UTF8
Copy-Item (Join-Path $PSScriptRoot "Assets") (Join-Path $stage "Assets") -Recurse -Force

# 5) makeappx 打包
$makeappx = Find-Tool "makeappx.exe"
$outDir = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$msix = Join-Path $outDir "LanChat_$version.msix"
Write-Host "makeappx pack → $msix"
& $makeappx pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack 失败（$LASTEXITCODE）" }

# 6) 签名（本机测试用自签名证书；Subject 必须与清单 Publisher 一致）
if (-not $NoSign) {
    $signtool = Find-Tool "signtool.exe"
    $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=mihuzuimin" `
        -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 2048 `
        -CertStoreLocation Cert:\CurrentUser\My `
        -FriendlyName "LanChat MSIX dev" -NotAfter (Get-Date).AddYears(5)
    $pfx = Join-Path $outDir "lanchat-dev.pfx"
    $cer = Join-Path $outDir "lanchat-dev.cer"
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $CertPassword -AsPlainText -Force) | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    & $signtool sign /f $pfx /p $CertPassword /fd SHA256 /v $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool sign 失败（$LASTEXITCODE）" }
}

Write-Host ""
Write-Host "完成：$msix"
if (-not $NoSign) {
    Write-Host "本机安装（管理员 PowerShell，先信任自签名证书再安装）："
    Write-Host "  Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Write-Host "  Add-AppxPackage -Path `"$msix`""
}
Write-Host "同机双实例联调若连不上（MSIX 回环隔离），以管理员运行："
Write-Host "  checknetisolation LoopbackExempt -a -n=(Get-AppxPackage *lanchat*).PackageFamilyName"
