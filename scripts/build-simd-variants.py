#!/usr/bin/env python3
"""Build isolated SIMD acceptance variants under ignored artifacts/ (never changes library sources).

Usage: build-simd-variants.py [all|no-dct|no-color|no-filters|no-output|no-dequant|no-quant|no-cfl|no-recursion ...]
Disabled dispatch helpers return zero/false at compile time, leaving their original scalar callers intact.
Use the same CPU/runtime settings for both variants; these are private measurement builds, not packages.
"""
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/simd-0.8.0/variants'
DISABLE = {
    'dct': ('Codestream/VarDct/JxlDct.Simd.cs', ['TransformColumnsSimd', 'ScaledIdct8x8Avx2', 'ScaledDct8x8Avx2', 'TransposeSimd', 'IdctColumns16Avx2', 'DctColumns16Avx2', 'TransformColumns128', 'AddRowAvx2', 'ScaleRowAvx2', 'CombineFirstAvx2', 'MergeRowsAvx2', 'SplitRowsAvx2']),
    'recursion': ('Codestream/VarDct/JxlDct.Simd.cs', ['AddRowAvx2', 'ScaleRowAvx2', 'CombineFirstAvx2', 'MergeRowsAvx2', 'SplitRowsAvx2']),
    'color': ('Codestream/Color/JxlColorOutput.Simd.cs', ['ApplyXybAvx2', 'YCbCrToRgbAvx2']),
    'filters': ('Codestream/Rendering/JxlRenderPipeline.Simd.cs', ['GaborishAvx2', 'EpfAvx2']),
    'output': ('Integration/JxlPixelWriter.Simd.cs', ['WriteWideRowSimd', 'WriteNarrowRowSimd']),
    'dequant': ('Codestream/VarDct/JxlVarDctFrame.Simd.cs', ['DequantizeAvx2']),
    'registers': ('Codestream/VarDct/JxlDct.Simd.cs', ['ScaledIdct8x8Avx2', 'ScaledDct8x8Avx2', 'TransposeSimd']),
    'entropy': ('Codestream/Encoding/Lossy/JxlAcStrategySearch.Simd.cs', ['SumChannelsAvx2']),
    'dct16': ('Codestream/VarDct/JxlDct.Simd.cs', ['IdctColumns16Avx2', 'DctColumns16Avx2']),
    'xyb': ('Codestream/Color/JxlXybInput.Simd.cs', ['ConvertAvx2']),
    'small': ('Codestream/VarDct/JxlDct.Simd.cs', ['TransformColumns128', 'Transpose4Sse41']),
    'afv': ('Codestream/VarDct/JxlDct.Simd.cs', ['AfvBasisAvx2']),
    'gather': ('Codestream/VarDct/JxlDct.Simd.cs', ['GatherAvx2']),
    'haar': ('Codestream/VarDct/JxlDct.Simd.cs', ['Dct2X2Sse41', 'ForwardDct2X2Sse41']),
    'quant': ('Codestream/Encoding/Lossy/JxlCoefficientQuantizer.Simd.cs', ['QuantizeRowSimd']),
    'cfl': ('Codestream/Encoding/Lossy/JxlChromaFromLumaSearch.Simd.cs', ['PrepareSlopesAvx2', 'DerivativesAvx2']),
}


def disable(text, name):
    # Locate a method declaration, skipping calls to the same method.
    import re
    match = re.search(r'(?:internal|private) (?:static )?(?:int|bool) ' + name + r'(?:<[^>]+>)?\(', text)
    if not match:
        raise ValueError(f'Method not found: {name}')
    start = text.index('{', match.end())
    depth, end = 1, start + 1
    while depth:
        depth += (text[end] == '{') - (text[end] == '}')
        end += 1
    result = 'false' if ' bool ' in match.group(0) else '0'
    return text[:start] + '{ return ' + result + '; }' + text[end:]


def main():
    for variant in sys.argv[1:] or ['all']:
        if variant != 'all' and variant.removeprefix('no-') not in DISABLE:
            raise ValueError(variant)
        dest = OUT / variant
        source = dest / 'source'
        shutil.copytree(ROOT / 'src', source / 'src', dirs_exist_ok=True, ignore=shutil.ignore_patterns('bin', 'obj'))
        shutil.copytree(ROOT / 'benchmarks/PictureSharp.Jxl.Speed', source / 'benchmarks/PictureSharp.Jxl.Speed',
                        dirs_exist_ok=True, ignore=shutil.ignore_patterns('bin', 'obj'))
        for name in ['Directory.Build.props', 'VERSION', 'NuGet.Config', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt']:
            shutil.copy2(ROOT / name, source / name)
        if variant != 'all':
            file, names = DISABLE[variant.removeprefix('no-')]
            path = source / 'src/PictureSharp.Jxl' / file
            text = path.read_text()
            for name in names:
                text = disable(text, name)
            path.write_text(text)
        subprocess.run(['dotnet', 'build', str(source / 'benchmarks/PictureSharp.Jxl.Speed'), '-c', 'Release', '-v', 'q',
                        '--artifacts-path', str(dest / 'build')], check=True)
        print(dest / 'build/bin/PictureSharp.Jxl.Speed/release/PictureSharp.Jxl.Speed.dll', flush=True)


if __name__ == '__main__':
    main()
