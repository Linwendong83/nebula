#!/usr/bin/env python3
"""Extract the verified shield DXBC from the user's installed game. Standard library only.

The original compiled kernel remains a locally generated deployment artifact, not copied source.
Unknown game kernels are rejected rather than guessed against a stale constant-buffer layout.
"""
import argparse
import hashlib
import mmap
from pathlib import Path
import struct

SHADER_SHA256 = 'a35923d287b3d6c5b02558f6fcadba04e157601c9cf84c24bdb03f3e467cff29'


def extract(game, destination):
    asset = game / 'DSPGAME_Data/resources.assets'
    with asset.open('rb') as source, mmap.mmap(source.fileno(), 0, access=mmap.ACCESS_READ) as data:
        offset = 0
        while True:
            offset = data.find(b'DXBC', offset)
            if offset < 0:
                raise RuntimeError('Verified shield shader not found. This game version requires revalidation.')
            if offset + 32 <= len(data):
                size = struct.unpack_from('<I', data, offset + 24)[0]
                if 32 <= size <= 1048576 and offset + size <= len(data):
                    shader = data[offset:offset + size]
                    if hashlib.sha256(shader).hexdigest() == SHADER_SHA256:
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        destination.write_bytes(shader)
                        print(f'Extracted {size} bytes from {asset}; SHA256={SHADER_SHA256}')
                        return
            offset += 4


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game-dir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    extract(args.game_dir, args.output)
