$AssemblyPath = 'C:\Program Files\Logi\LogiPluginService\PluginApi.dll'
if (Test-Path $AssemblyPath) {
    $asm = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $type = $asm.GetTypes() | Where-Object { $_.Name -eq 'PluginDynamicCommand' }
    if ($type) {
        $type.GetMethods() | Select-Object Name, ReturnType, IsVirtual | Format-Table -AutoSize
    }
    else {
        Write-Host "Type not found"
    }
}
else {
    Write-Host "File not found"
}
