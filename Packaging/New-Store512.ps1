# 生成商店提交用 512x512 图标（与 New-LogoAssets.ps1 同款式：深底 + 橙色气泡 + LC）
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot "Assets"
$w = 512; $h = 512
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::FromArgb(255, 0x2A, 0x2A, 0x2E))

$mainBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xF7, 0x72, 0x26))
$whiteBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

$pad = [int]($w * 0.16)
$rect = New-Object System.Drawing.Rectangle($pad, $pad, ($w - 2 * $pad), ($h - 2 * $pad - [int]($h * 0.06)))
$radius = [int]([Math]::Min($rect.Width, $rect.Height) * 0.28)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$d = $radius * 2
$path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
$path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
$path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
$path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
$path.CloseFigure()

# 气泡尾巴（Points 数组）
$p1 = New-Object System.Drawing.Point(($rect.X + [int]($rect.Width * 0.22)), $rect.Bottom)
$p2 = New-Object System.Drawing.Point(($rect.X + [int]($rect.Width * 0.48)), $rect.Bottom)
$p3 = New-Object System.Drawing.Point(($rect.X + [int]($rect.Width * 0.18)), ($rect.Bottom + [int]($h * 0.07)))
$tail = New-Object System.Drawing.Point[] 3
$tail[0] = $p1; $tail[1] = $p2; $tail[2] = $p3
$path.AddPolygon($tail)
$g.FillPath($mainBrush, $path)

$font = New-Object System.Drawing.Font("Segoe UI", ([int]($w * 0.24)), [System.Drawing.FontStyle]::Bold)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
$txt = New-Object System.Drawing.RectangleF(0, 0, [float]$w, [float]($h - ($h * 0.02)))
$g.DrawString("LC", $font, $whiteBrush, $txt, $sf)

$out = Join-Path $assets "StoreLogo512.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "generated StoreLogo512.png ($((Get-Item $out).Length) bytes)"
