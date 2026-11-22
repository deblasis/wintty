# Fails when the release DLL carries EVEX/AVX-512 in code that is not
# runtime-dispatched. Allowlisted families, all of which pick AVX-512 per
# machine at run time and are safe on an AVX2-only CPU:
#   - MSVC CRT string routines (memcpy/memset/memmove)
#   - simdutf's icelake kernels (get_active_implementation; re-enabled
#     354d59dcd, ~4k sites in a baseline ReleaseFast DLL)
#   - MSVC STL vector_algorithms.cpp (CPUID-dispatched __std_* entry
#     points and their _Minmax_traits_8_avx/_sse helpers)
param(
    [string]$Dll = 'zig-out/lib/ghostty.dll',
    [string]$Pdb = 'zig-out/lib/ghostty.pdb'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Dll)) { throw "missing DLL: $Dll" }
if (-not (Test-Path -LiteralPath $Pdb)) { throw "missing PDB: $Pdb" }

$objdump = (Get-Command llvm-objdump.exe -ErrorAction SilentlyContinue) ??
           (Get-Command llvm-objdump -ErrorAction SilentlyContinue)
$symbolizer = (Get-Command llvm-symbolizer.exe -ErrorAction SilentlyContinue) ??
              (Get-Command llvm-symbolizer -ErrorAction SilentlyContinue)
$readobj = (Get-Command llvm-readobj.exe -ErrorAction SilentlyContinue) ??
           (Get-Command llvm-readobj -ErrorAction SilentlyContinue)
if (-not $objdump) { throw 'llvm-objdump not found; install LLVM and put it on PATH' }
if (-not $symbolizer) { throw 'llvm-symbolizer not found; install LLVM and put it on PATH' }
if (-not $readobj) { throw 'llvm-readobj not found; install LLVM and put it on PATH' }

$machine = (& $readobj.Source --file-headers $Dll | Select-String -Pattern 'Machine: (IMAGE_FILE_MACHINE_[A-Z0-9_]+)').Matches.Groups[1].Value
if ($machine -ne 'IMAGE_FILE_MACHINE_AMD64') {
    throw "release DLL is $machine, expected IMAGE_FILE_MACHINE_AMD64"
}

$tmp = [System.IO.Path]::GetTempFileName()
try {
    $lines = @(& $objdump.Source -d $Dll 2>&1)
    if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0 -or
        -not ($lines | Select-String -Pattern '^\s*[0-9a-fA-F]+:' -Quiet)) {
        throw "llvm-objdump failed (exit $LASTEXITCODE) or produced no instruction lines"
    }
    $lines | Set-Content -LiteralPath $tmp
    # EVEX instructions carry the 0x62 prefix. Data bytes can start with 0x62
    # too, but llvm-objdump renders those as <unknown>; a decoded EVEX line is
    # the complete detector. zmm/mask/mnemonics remain as a belt-and-braces
    # pass for targets that print them without raw bytes.
    $hits = Select-String -Path $tmp -Pattern '^\s*[0-9a-fA-F]+:\s+62\s(?!.*<unknown>)|zmm[0-9]|\{%k[0-7]\}'
    if (-not $hits) {
        Write-Host 'baseline ISA check: no EVEX/AVX-512 instructions'
        exit 0
    }

    $bad = @()
    $addrs = foreach ($hit in $hits) {
        if ($hit.Line -match '^\s*([0-9a-fA-F]+):') { '0x' + $Matches[1] }
        else { $bad += $hit.Line }
    }
    if ($addrs) {
        # One symbolizer process for every site (a spawn per site was the
        # old minutes-long runtime). Each address yields a record of
        # inline frames, one "name" and one "file:line" per frame,
        # terminated by a blank line. A site is allowlisted when ANY frame
        # matches: an inlined simdutf icelake kernel can report an outer
        # plain-c name, and the STL minmax dispatch table mixes its
        # AVX512 body into the shared _sse helper.
        $out = $addrs | & $symbolizer.Source --obj=$Dll --functions=short 2>$null
        $records = (@($out) -join "`n") -split "`n`n"
        for ($i = 0; $i -lt $addrs.Count; $i++) {
            $names = @($records[$i] -split "`n" | Where-Object { $_ })
            if (-not $names -or -not ($names | Where-Object { $_ -match
                    '^(memcpy|memset|memmove)_avx512|^simdutf::icelake::|__std_|_Minmax_traits_8_(avx|sse)::' })) {
                $bad += "$($addrs[$i]) $($names[0])"
            }
        }
    }

    if ($bad.Count -gt 0) {
        throw ("EVEX/AVX-512 outside runtime-dispatched routines:`n  " + ($bad -join "`n  "))
    }
    Write-Host "baseline ISA check: $($hits.Count) EVEX site(s), all runtime-dispatched (CRT, simdutf icelake, STL vector_algorithms)"
}
finally {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
}
