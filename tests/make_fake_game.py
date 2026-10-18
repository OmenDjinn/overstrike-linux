#!/usr/bin/env python3
"""
Builds a tiny fake "Marvel's Spider-Man 2" install for testing overstrike-cli without the real game.

It writes, independently of Overstrike's own C# code:
  - 'toc'  : DAT1 container in the i29/i30 layout (spans, asset ids, size entries, archives map, ...)
  - 'd/dat/a0' : a DSAR archive holding two vanilla assets, one in a GDeflate block and one in an LZ4 block
  - 'Spider-Man2.exe' : dummy file (Overstrike checks it exists)
and a couple of test mods.

The toc refers to the archive as "d\\Dat\\a0" (Windows separators + different case), like real tocs do,
to exercise the Linux path handling.
"""
import ctypes, io, json, os, struct, sys, zipfile

GDEFLATE_LIB = sys.argv[3] if len(sys.argv) > 3 else None

TAG_SPANS = 0xEDE8ADA9
TAG_ASSET_IDS = 0x506D7B8A
TAG_SIZES = 0x65BCF461
TAG_ARCHIVES = 0x398ABFF0
TAG_HEADERS = 0x654BDED9
TAG_KEY_ASSETS = 0x6D921D7B
TAG_TEX_HEADER = 0x62297090
TAG_TEX_IDS = 0x36A6C8CC
TAG_TEX_META = 0xC9FB9DDA

VANILLA_A = 0x1111222233334444  # span 0, GDeflate block
VANILLA_B = 0x9999AAAABBBBCCCC  # span 0, LZ4 block
NUM_SPANS = 8


def gdeflate_tile_stream(data: bytes) -> bytes:
    """Compress with NVIDIA libdeflate GDeflate and wrap in DirectStorage's TileStream format."""
    lib = ctypes.CDLL(GDEFLATE_LIB)
    lib.libdeflate_alloc_gdeflate_compressor.restype = ctypes.c_void_p
    lib.libdeflate_alloc_gdeflate_compressor.argtypes = [ctypes.c_int]
    lib.libdeflate_gdeflate_compress_bound.restype = ctypes.c_size_t
    lib.libdeflate_gdeflate_compress_bound.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]

    class OutPage(ctypes.Structure):
        _fields_ = [("data", ctypes.c_void_p), ("nbytes", ctypes.c_size_t)]

    lib.libdeflate_gdeflate_compress.restype = ctypes.c_size_t
    lib.libdeflate_gdeflate_compress.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_size_t, ctypes.POINTER(OutPage), ctypes.c_size_t]

    comp = lib.libdeflate_alloc_gdeflate_compressor(9)
    npages = ctypes.c_size_t(0)
    bound = lib.libdeflate_gdeflate_compress_bound(comp, len(data), ctypes.byref(npages))
    per_page = bound // npages.value
    bufs = [ctypes.create_string_buffer(per_page) for _ in range(npages.value)]
    pages = (OutPage * npages.value)(*[OutPage(ctypes.cast(b, ctypes.c_void_p), per_page) for b in bufs])
    total = lib.libdeflate_gdeflate_compress(comp, data, len(data), pages, npages.value)
    assert total > 0, "gdeflate compression failed"
    tiles = [bytes(bufs[i].raw[:pages[i].nbytes]) for i in range(npages.value)]

    n = len(tiles)
    out = io.BytesIO()
    out.write(struct.pack("<BBH", 4, 4 ^ 0xFF, n))
    last_tile_size = len(data) - (n - 1) * 65536
    out.write(struct.pack("<I", (1 & 3) | ((last_tile_size & 0x3FFFF) << 2)))
    # tileOffsets[0] = size of last tile, tileOffsets[i] = offset of tile i
    offsets = [len(tiles[-1])]
    pos = 0
    for i in range(1, n):
        pos += len(tiles[i - 1])
        offsets.append(pos)
    for o in offsets:
        out.write(struct.pack("<I", o))
    for t in tiles:
        out.write(t)
    return out.getvalue()


def lz4_block(data: bytes) -> bytes:
    """Minimal valid LZ4 block: all literals, no matches."""
    n = len(data)
    out = bytearray()
    token_lit = 15 if n >= 15 else n
    out.append(token_lit << 4)
    if n >= 15:
        rem = n - 15
        while rem >= 255:
            out.append(255)
            rem -= 255
        out.append(rem)
    out += data
    return bytes(out)


def make_dsar(blocks):
    """blocks: list of (compression_type, real_bytes, compressed_bytes)."""
    header_end = 32 + 32 * len(blocks)
    out = io.BytesIO()
    total_real = sum(len(b[1]) for b in blocks)
    out.write(struct.pack("<IIII", 0x52415344, 0x10003, len(blocks), header_end))
    out.write(struct.pack("<Q", total_real))
    out.write(b"PADDING*")
    comp_off = header_end
    real_off = 0
    for ctype, real, comp in blocks:
        out.write(struct.pack("<QQIIB", real_off, comp_off, len(real), len(comp), ctype))
        out.write(b"\x55" * 7)
        comp_off += len(comp)
        real_off += len(real)
    for _, _, comp in blocks:
        out.write(comp)
    return out.getvalue()


def make_dat1(type_magic, sections):
    """sections: dict tag -> bytes"""
    tags = sorted(sections)
    count = len(tags)
    offset = 16 + 12 * count
    table = []
    body = io.BytesIO()
    for tag in tags:
        if offset % 16:
            pad = 16 - offset % 16
            body.write(b"\0" * pad)
            offset += pad
        table.append((tag, offset, len(sections[tag])))
        body.write(sections[tag])
        offset += len(sections[tag])
    out = io.BytesIO()
    out.write(struct.pack("<IIIHH", 0x44415431, type_magic, offset, count, 0))
    for tag, off, size in table:
        out.write(struct.pack("<III", tag, off, size))
    out.write(body.getvalue())
    return out.getvalue()


def archive_entry(name: str) -> bytes:
    fn = name.encode("ascii").ljust(40, b"\0")
    return fn + struct.pack("<QQIHI", 0, 0, 0, 0, 0)


def main():
    game = sys.argv[1]
    mods = sys.argv[2]
    os.makedirs(os.path.join(game, "d", "dat"), exist_ok=True)
    os.makedirs(mods, exist_ok=True)

    # vanilla assets: 36-byte header + payload (span 0 assets carry a header)
    asset_a = b"HDR-A".ljust(36, b"\0") + bytes((i * 7) % 251 for i in range(150_000))  # >64KB: multiple GDeflate tiles
    asset_b = b"HDR-B".ljust(36, b"\0") + b"vanilla asset B, stored in an LZ4 block. " * 20

    blocks = [(2, asset_a, gdeflate_tile_stream(asset_a)), (3, asset_b, lz4_block(asset_b))]
    with open(os.path.join(game, "d", "dat", "a0"), "wb") as f:
        f.write(make_dsar(blocks))

    ids = sorted([VANILLA_A, VANILLA_B])
    sizes = {VANILLA_A: (len(asset_a), 0, 0, -1), VANILLA_B: (len(asset_b), 0, len(asset_a), -1)}
    spans = [(0, 2)] + [(2, 0)] * (NUM_SPANS - 1)

    sections = {
        TAG_SPANS: b"".join(struct.pack("<II", a, c) for a, c in spans),
        TAG_ASSET_IDS: b"".join(struct.pack("<Q", i) for i in ids),
        TAG_SIZES: b"".join(struct.pack("<IIIi", *sizes[i]) for i in ids),
        TAG_ARCHIVES: archive_entry("d\\Dat\\a0"),
        TAG_HEADERS: b"",
        TAG_KEY_ASSETS: b"",
        TAG_TEX_HEADER: struct.pack("<I", 0),
        TAG_TEX_IDS: b"",
        TAG_TEX_META: b"",
    }
    dat1 = make_dat1(0x51B8E006, sections)
    with open(os.path.join(game, "toc"), "wb") as f:
        f.write(struct.pack("<II", 0x34E89035, len(dat1)))
        f.write(dat1)

    with open(os.path.join(game, "Spider-Man2.exe"), "wb") as f:
        f.write(b"MZ fake exe for tests")

    # stage mod: replaces vanilla A (Windows-style '\' entry names) and adds a new asset in span 1
    new_a = b"NEWHDR".ljust(36, b"\0") + b"modded asset A payload " * 100
    new_c = b"brand new asset in span 1 " * 40
    with zipfile.ZipFile(os.path.join(mods, "test_stage.stage"), "w") as z:
        z.writestr("info.json", json.dumps({"game": "MSM2", "name": "Test Stage", "author": "overstrike-cli tests"}))
        zi = zipfile.ZipInfo(f"0\\{VANILLA_A:016X}")  # as a Windows tool would store it
        zi.create_system = 0
        z.writestr(zi, new_a)
        z.writestr("1/5555666677778888", new_c)

    # second stage mod inside a .zip archive (tests '||' nested paths)
    inner = io.BytesIO()
    with zipfile.ZipFile(inner, "w") as z:
        z.writestr("info.json", json.dumps({"game": "MSM2", "name": "Nested Stage"}))
        z.writestr("1/AAAABBBBCCCCDDDD", b"nested mod data " * 10)
    with zipfile.ZipFile(os.path.join(mods, "nested_pack.zip"), "w") as z:
        z.writestr("Some Folder\\nested.stage", inner.getvalue())

    # modular mod: module "Color" (red/blue -> different asset data), module "Extra" (none/on)
    def stage_bytes(name, files):
        b = io.BytesIO()
        with zipfile.ZipFile(b, "w") as z:
            z.writestr("info.json", json.dumps({"game": "MSM2", "name": name}))
            for k, v in files.items():
                z.writestr(k, v)
        return b.getvalue()
    with zipfile.ZipFile(os.path.join(mods, "test.modular"), "w") as z:
        z.writestr("info.json", json.dumps({
            "format_version": 1, "game": "MSM2", "name": "Test Modular",
            "layout": [
                ["header", "Pick things"],
                ["module", "Color", [["", "Red", "red.stage"], ["", "Blue", "blue.stage"]]],
                ["module", "Always", [["", "Only", "always.stage"]]],
                ["module", "Extra", [["", "None", ""], ["", "On", "extra.stage"]]],
            ]}))
        z.writestr("red.stage", stage_bytes("red", {"1/0000000000000C01": b"RED " * 8}))
        z.writestr("blue.stage", stage_bytes("blue", {"1/0000000000000C01": b"BLUE" * 8}))
        z.writestr("always.stage", stage_bytes("always", {"1/0000000000000A11": b"ALWAYS" * 4}))
        z.writestr("extra.stage", stage_bytes("extra", {"1/000000000000E0E0": b"EXTRA" * 4}))

    json.dump({
        "red": (b"RED " * 8).hex(), "blue": (b"BLUE" * 8).hex(), "always": (b"ALWAYS" * 4).hex(), "extra": (b"EXTRA" * 4).hex(),
        "vanilla_a": asset_a.hex(), "vanilla_b": asset_b.hex(),
        "new_a": new_a.hex(), "new_c": new_c.hex(),
    }, open(os.path.join(mods, "expected.json"), "w"))
    print("fake game written to", game)


if __name__ == "__main__":
    main()
