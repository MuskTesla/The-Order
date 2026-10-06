param([Parameter(Mandatory)][string]$Action,[string]$Value,[string]$Id,[int]$Width=1160,[int]$Height=820)
$ErrorActionPreference='Stop'
$visualControl=Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path '.artifacts/phase7-2a/control'
$visualNonce=[guid]::NewGuid().ToString('N')
$visualRequest=@{nonce=$visualNonce;action=$Action}
if($Action -in @('solid','motion')){$visualRequest.value=$Value -eq 'true'}
elseif($Action -eq 'galleryStep'){$visualRequest.value=[double]$Value}
elseif($PSBoundParameters.ContainsKey('Value')){$visualRequest.value=$Value}
if($Id){$visualRequest.id=$Id}
if($Action -eq 'resize'){$visualRequest.width=$Width;$visualRequest.height=$Height}
$visualTemporary=Join-Path $visualControl 'request.tmp'
$visualRequest | ConvertTo-Json | Set-Content -LiteralPath $visualTemporary -Encoding utf8
Move-Item -LiteralPath $visualTemporary -Destination (Join-Path $visualControl 'request.json') -Force
if($Action -eq 'exit'){return}
$visualDeadline=[DateTime]::UtcNow.AddSeconds(25)
while([DateTime]::UtcNow -lt $visualDeadline){
 Start-Sleep -Milliseconds 200
 $visualReceiptFile=Join-Path $visualControl 'receipt.json'
 if(Test-Path -LiteralPath $visualReceiptFile){try{$visualReceipt=Get-Content -LiteralPath $visualReceiptFile -Raw | ConvertFrom-Json}catch{continue}
 if($visualReceipt.nonce -eq $visualNonce){if(!$visualReceipt.success){throw "Visual command failed: $($visualReceipt.error)"};$visualReceipt | ConvertTo-Json -Depth 15;return}}
}
throw "Visual command timed out: $Action"
