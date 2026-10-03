# Fetches the browser libraries the PDF reader needs into src\wwwroot\vendor.
#
# They are vendored rather than loaded from a CDN because an "offline copy" that needs the internet
# to render defeats its own purpose, and this runs on a private network. They are NOT committed -
# run this once after cloning, and again to upgrade.
#
#   powershell -ExecutionPolicy Bypass -File fetch-vendor.ps1

$ErrorActionPreference = 'Stop'
$dest = Join-Path $PSScriptRoot 'src\wwwroot\vendor'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# pdf.js: taken from the npm distribution, NOT cdnjs - cdnjs does not carry every release
# (it 404s on 6.4.299, the version this was built against).
$pdfjs = (Invoke-RestMethod 'https://registry.npmjs.org/pdfjs-dist/latest' -TimeoutSec 60).version
Write-Output "pdfjs-dist $pdfjs"
foreach ($f in 'pdf.min.mjs', 'pdf.worker.min.mjs') {
    Invoke-WebRequest "https://cdn.jsdelivr.net/npm/pdfjs-dist@$pdfjs/build/$f" `
        -OutFile (Join-Path $dest $f) -UseBasicParsing -TimeoutSec 180
    Write-Output ("  {0,-22} {1} KB" -f $f, [math]::Round((Get-Item (Join-Path $dest $f)).Length / 1KB))
}

# pdf-lib: used only to burn highlights into an exported copy, client-side.
Invoke-WebRequest 'https://cdnjs.cloudflare.com/ajax/libs/pdf-lib/1.17.1/pdf-lib.min.js' `
    -OutFile (Join-Path $dest 'pdf-lib.min.js') -UseBasicParsing -TimeoutSec 180
Write-Output ("  {0,-22} {1} KB" -f 'pdf-lib.min.js', [math]::Round((Get-Item (Join-Path $dest 'pdf-lib.min.js')).Length / 1KB))

Write-Output "`nDone. If pdf.js is upgraded, re-check two things in wwwroot\index.html:"
Write-Output "  * the TextLayer API (renderTextLayer was removed in v6)"
Write-Output "  * the CSS scale custom property (--scale-factor became --total-scale-factor)"
