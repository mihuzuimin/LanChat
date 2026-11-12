# 生成 MSIX 包所需的 Logo PNG（橙色主色 + 白字 LC，按 scale-100 尺寸输出）。
# 仅在更换图标设计时手动运行；产物 PNG 提交入库供打包脚本引用。
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot "Assets"
New-Item -ItemType Directory -Force -Path $assets | Out-Null

$bg   = [System.Drawing.Color]::FromArgb(255, 0x2A, 0x2A, 0x2E)   # 深色底
$main = [System.Drawing.Color]::FromArgb(255, 0xF7, 0x72, 0x26)   # 橙色主色
$white = [System.Drawing.Color]::White

function New-Logo([string]$file, [int]$w, [int]$h, [double]$fontScale, [bool]$splash) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear($bg)

    # 橙色圆角方块内嵌对话气泡形状：用圆角矩形 + 底部小三角近似
    $pad = [int]([Math]::Min($w, $h) * 0.16)
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($w - 2 * $pad), ($h - 2 * $pad - [int]($h * 0.06)))
    $radius = [int]([Math]::Min($rect.Width, $rect.Height) * 0.28)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    # 尾巴
    $tail = @(
        (New-Object System.Drawing.Point(($rect.X + [int]($rect.Width * 0.22)), $rect.Bottom)),
        (New-Object System.Drawing.Point(($rect.X + [int]($rect.Width * 0.48)), $rect.Bottom)),
        (New-Object System.Drawing.Point(($rect.X + [int]($rect.Width * 0.18)), ($rect.Bottom + [int]($h * 0.07))))
    )
    $path.AddPolygon($tail)
    $g.FillPath((New-Object System.Drawing.SolidBrush($main)), $path)

    if ($splash) {
        # 启动图：右侧放 "LanChat" 文字
        $font = New-Object System.Drawing.Font("Segoe UI", ([Math]::Max(16, [int]($h * 0.22))), [System.Drawing.FontStyle]::Bold)
        $brush = New-Object System.Drawing.SolidBrush($white)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
        $txtRect = New-Object System.Drawing.RectangleF([float]($w * 0.42), 0, [float]($w * 0.55), [float]$h)
        $g.DrawString("LanChat", $font, $brush, $txtRect, $sf)
    } else {
        # 方形图标：气泡内白字 LC
        $font = New-Object System.Drawing.Font("Segoe UI", ([Math]::Max(9, [int]([Math]::Min($w, $h) * $fontScale))), [System.Drawing.FontStyle]::Bold)
        $brush = New-Object System.Drawing.SolidBrush($white)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
        $txtRect2 = New-Object System.Drawing.RectangleF(0, 0, [float]$w, [float]($h - ($h * 0.04)))
        $g.DrawString("LC", $font, $brush, $txtRect2, $sf)
    }

    $bmp.Save((Join-Path $assets $file), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "generated $file ($w x $h)"
}

New-Logo "StoreLogo.png"     50   50  0.30 $false
New-Logo "Square44x44Logo.png"   44   44  0.30 $false
New-Logo "Square71x71Logo.png"   71   71  0.30 $false
New-Logo "Square150x150Logo.png" 150 150  0.26 $false
New-Logo "Square310x310Logo.png" 310 310  0.24 $false
New-Logo "Wide310x150Logo.png"   310 150  0.26 $false
New-Logo "SplashScreen.png"      620 300  0.26 $true
