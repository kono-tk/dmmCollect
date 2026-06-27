$shell = New-Object -ComObject WScript.Shell
Get-ChildItem M:\SEARCH\FLAT\*.lnk | ForEach-Object {
    $lnk = $shell.CreateShortcut($_.FullName)
    $target = $lnk.TargetPath
    if (Test-Path $target) {
        $targetMTime = (Get-Item $target).LastWriteTime
        $lnkMTime = $_.LastWriteTime
        if ($lnkMTime -gt (Get-Date)) {
            Write-Output "Lnk: $($_.Name) (LnkTime: $lnkMTime) -> TargetDir: $(Split-Path $target -Leaf) (TargetTime: $targetMTime)"
        }
    }
}
