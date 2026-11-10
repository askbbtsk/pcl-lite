param()
$ErrorActionPreference = 'Stop'
$shaderSource = Join-Path $PSScriptRoot 'Glass.hlsl'
$shaderOutput = Join-Path $PSScriptRoot 'Glass.ps'
$fxcCommand = Get-Command fxc.exe -ErrorAction SilentlyContinue
$fxcPath = if ($fxcCommand) { $fxcCommand.Source } else { $null }
if (-not $fxcPath) {
    $sdkPaths = @('C:\Program Files (x86)\Windows Kits\10\bin', 'C:\Program Files\Windows Kits\10\bin')
    foreach ($sdkPath in $sdkPaths) {
        if (Test-Path -LiteralPath $sdkPath) {
            $candidate = Get-ChildItem -LiteralPath $sdkPath -Filter fxc.exe -File -Recurse |
                Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
            if ($candidate) { $fxcPath = $candidate.FullName; break }
        }
    }
}
if ($fxcPath) {
    & $fxcPath /T ps_3_0 /E main /O3 /Fo $shaderOutput $shaderSource
    if ($LASTEXITCODE -ne 0) { throw 'fxc compilation failed.' }
} else {
    if (-not ('TahoeShaderCompiler' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
public static class TahoeShaderCompiler {
    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBlob {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] UIntPtr GetBufferSize();
    }
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] data, UIntPtr size, string sourceName, IntPtr defines,
        IntPtr include, string entry, string target, uint flags, uint effectFlags,
        out IntPtr code, out IntPtr errors);
    public static void Compile(string input, string output) {
        byte[] source = File.ReadAllBytes(input);
        IntPtr code = IntPtr.Zero, errors = IntPtr.Zero;
        try {
            int hr = D3DCompile(source, (UIntPtr)source.Length, input, IntPtr.Zero, IntPtr.Zero,
                "main", "ps_3_0", 1u << 15, 0, out code, out errors);
            string diagnostics = "";
            if (errors != IntPtr.Zero) {
                IBlob blob = (IBlob)Marshal.GetObjectForIUnknown(errors);
                diagnostics = Marshal.PtrToStringAnsi(blob.GetBufferPointer(), checked((int)blob.GetBufferSize().ToUInt64()));
                Marshal.ReleaseComObject(blob);
            }
            if (hr < 0) throw new InvalidOperationException(diagnostics + " HRESULT=" + hr.ToString("X8"));
            if (diagnostics.Length > 0) Console.WriteLine(diagnostics);
            IBlob compiled = (IBlob)Marshal.GetObjectForIUnknown(code);
            byte[] bytes = new byte[checked((int)compiled.GetBufferSize().ToUInt64())];
            Marshal.Copy(compiled.GetBufferPointer(), bytes, 0, bytes.Length);
            File.WriteAllBytes(output, bytes);
            Marshal.ReleaseComObject(compiled);
        } finally {
            if (code != IntPtr.Zero) Marshal.Release(code);
            if (errors != IntPtr.Zero) Marshal.Release(errors);
        }
    }
}
'@
    }
    [TahoeShaderCompiler]::Compile($shaderSource, $shaderOutput)
}
Write-Host "Compiled ps_3_0/main: $shaderOutput ($((Get-Item -LiteralPath $shaderOutput).Length) bytes)"
