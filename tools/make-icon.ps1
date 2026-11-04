# TaskMonitor 아이콘을 그려 TaskMonitor\app.ico(16·24·32·48·256px, PNG 항목)로 쓴다.
# 다시 만들 때: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\TaskMonitor\app.ico"
$sizes = 16, 24, 32, 48, 256

function Render([int]$n) {
  $bmp = New-Object System.Drawing.Bitmap $n, $n
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)
  $r = [single]($n * 0.22)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $w = [single]($n - 1)
  $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($w - $r, 0, $r, $r, 270, 90)
  $path.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $path.AddArc(0, $w - $r, $r, $r, 90, 90); $path.CloseFigure()
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 95, 184))), $path)
  # 부하 그래프: 낮게 가다 치솟고 내려오는 선(CPU 원인을 찾는 도구라는 뜻)
  $pts = @(@(0.14, 0.70), @(0.32, 0.62), @(0.46, 0.66), @(0.58, 0.24), @(0.70, 0.56), @(0.86, 0.46))
  $points = $pts | ForEach-Object { New-Object System.Drawing.PointF ([single]($_[0] * $n)), ([single]($_[1] * $n)) }
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][math]::Max(1.6, $n * 0.09))
  $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
  $g.DrawLines($pen, [System.Drawing.PointF[]]$points)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  if ($n -ge 256) {
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  } else {
    # 작은 크기는 고전 DIB 항목으로 쓴다(System.Drawing·WinForms는 작은 PNG 항목을 읽지 못한다).
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$n); $w.Write([int32]($n * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $n - 1; $y -ge 0; $y--) {
      for ($x = 0; $x -lt $n; $x++) { $c = $bmp.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A) }
    }
    $maskRow = [int]([math]::Ceiling($n / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $n)))
    $w.Flush()
  }
  $bmp.Dispose()
  return ,$ms.ToArray()
}

$images = $sizes | ForEach-Object { ,(Render $_) }
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $n = $sizes[$i]; $data = $images[$i]
  $bw.Write([byte]($(if ($n -ge 256) { 0 } else { $n }))); $bw.Write([byte]($(if ($n -ge 256) { 0 } else { $n })))
  $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
  $offset += $data.Length
}
foreach ($data in $images) { $bw.Write([byte[]]$data) }
$bw.Dispose()
"wrote $out ($((Get-Item $out).Length) bytes)"
