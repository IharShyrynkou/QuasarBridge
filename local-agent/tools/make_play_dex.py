"""Build a tiny classes.dex that plays one wav through Android MediaPlayer."""

import hashlib
import struct
import zlib
from pathlib import Path


def uleb(value: int) -> bytes:
    out = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        if value:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            return bytes(out)


def mutf8(text: str) -> bytes:
    raw = text.encode("utf-8")
    return uleb(len(text)) + raw + b"\0"


class DexBuilder:
    def __init__(self) -> None:
        self.strings: list[str] = []
        self.types: list[str] = []
        self.protos: list[tuple[str, tuple[str, ...]]] = []
        self.methods: list[tuple[str, str, tuple[str, tuple[str, ...]]]] = []

    def string(self, text: str) -> int:
        if text not in self.strings:
            self.strings.append(text)
        return self.strings.index(text)

    def type(self, descriptor: str) -> int:
        self.string(descriptor)
        if descriptor not in self.types:
            self.types.append(descriptor)
        return self.types.index(descriptor)

    def proto(self, ret: str, args: tuple[str, ...] = ()) -> int:
        self.type(ret)
        for arg in args:
            self.type(arg)
        key = (ret, args)
        if key not in self.protos:
            self.protos.append(key)
        return self.protos.index(key)

    def method(self, owner: str, name: str, ret: str, args: tuple[str, ...] = ()) -> int:
        self.type(owner)
        self.string(name)
        proto = (ret, args)
        self.proto(ret, args)
        key = (owner, name, proto)
        if key not in self.methods:
            self.methods.append(key)
        return self.methods.index(key)

    def build(self, code: bytes, registers: int, ins: int, outs: int) -> bytes:
        self.strings = sorted(set(self.strings))
        self.types = sorted(set(self.types), key=lambda item: self.strings.index(item))
        self.protos = sorted(self.protos, key=lambda item: (self.types.index(item[0]), item[1]))
        self.methods = sorted(
            self.methods,
            key=lambda item: (
                self.types.index(item[0]),
                self.strings.index(item[1]),
                self.protos.index(item[2]),
            ),
        )

        def sid(text: str) -> int:
            return self.strings.index(text)

        def tid(descriptor: str) -> int:
            return self.types.index(descriptor)

        def pid(ret: str, args: tuple[str, ...]) -> int:
            return self.protos.index((ret, args))

        def mid(owner: str, name: str, ret: str, args: tuple[str, ...] = ()) -> int:
            return self.methods.index((owner, name, (ret, args)))

        # Rebuild instruction stream with final indexes. The caller passes a function via closure
        # by formatting placeholders. Here `code` is already final because we resolve after sort
        # in the helper below. This method expects `code` to be produced by `finish`.
        raise NotImplementedError


def build_play_dex(path: str) -> bytes:
    strings = [
        "<init>",
        "LPlay;",
        "Landroid/media/MediaPlayer;",
        "Ljava/lang/Object;",
        "Ljava/lang/String;",
        "Ljava/lang/Thread;",
        "V",
        "I",
        "J",
        "[Ljava/lang/String;",
        "main",
        "setAudioStreamType",
        "setDataSource",
        "prepare",
        "start",
        "getDuration",
        "release",
        "sleep",
        "VI",
        "VJ",
        "VL",
        path,
    ]
    strings = sorted(set(strings))

    def sid(text: str) -> int:
        return strings.index(text)

    types = sorted(
        [
            "LPlay;",
            "Landroid/media/MediaPlayer;",
            "Ljava/lang/Object;",
            "Ljava/lang/String;",
            "Ljava/lang/Thread;",
            "V",
            "I",
            "J",
            "[Ljava/lang/String;",
        ],
        key=sid,
    )

    def tid(text: str) -> int:
        return types.index(text)

    protos = [
        ("V", ()),
        ("V", ("I",)),
        ("V", ("Ljava/lang/String;",)),
        ("V", ("J",)),
        ("I", ()),
        ("V", ("[Ljava/lang/String;",)),
    ]
    protos = sorted(protos, key=lambda item: (tid(item[0]), [tid(arg) for arg in item[1]]))

    def pid(ret: str, args: tuple[str, ...] = ()) -> int:
        return protos.index((ret, args))

    methods = [
        ("Landroid/media/MediaPlayer;", "<init>", ("V", ())),
        ("Landroid/media/MediaPlayer;", "setAudioStreamType", ("V", ("I",))),
        ("Landroid/media/MediaPlayer;", "setDataSource", ("V", ("Ljava/lang/String;",))),
        ("Landroid/media/MediaPlayer;", "prepare", ("V", ())),
        ("Landroid/media/MediaPlayer;", "start", ("V", ())),
        ("Landroid/media/MediaPlayer;", "getDuration", ("I", ())),
        ("Landroid/media/MediaPlayer;", "release", ("V", ())),
        ("Ljava/lang/Thread;", "sleep", ("V", ("J",))),
        ("LPlay;", "main", ("V", ("[Ljava/lang/String;",))),
    ]
    methods = sorted(methods, key=lambda item: (tid(item[0]), sid(item[1]), pid(item[2][0], item[2][1])))

    def mid(owner: str, name: str, ret: str, args: tuple[str, ...] = ()) -> int:
        return methods.index((owner, name, (ret, args)))

    def invoke(opcode: int, count: int, method: int, regs: list[int]) -> bytes:
        padded = (regs + [0, 0, 0, 0, 0])[:5]
        first = opcode | ((count << 4) << 8)
        # first code unit is little-endian: low byte opcode, high byte (count << 4) | fifth
        word0 = opcode | (((count << 4) | 0) << 8)
        word1 = method
        word2 = padded[0] | (padded[1] << 4) | (padded[2] << 8) | (padded[3] << 12)
        return struct.pack("<HHH", word0, word1, word2)

    insns = b"".join(
        [
            struct.pack("<HH", 0x0022, tid("Landroid/media/MediaPlayer;")),  # new-instance v0
            invoke(0x70, 1, mid("Landroid/media/MediaPlayer;", "<init>", "V"), [0]),
            struct.pack("<H", 0x3112),  # const/4 v1, #3 STREAM_MUSIC
            invoke(0x6E, 2, mid("Landroid/media/MediaPlayer;", "setAudioStreamType", "V", ("I",)), [0, 1]),
            struct.pack("<HH", 0x011A, sid(path)),  # const-string v1, path
            invoke(0x6E, 2, mid("Landroid/media/MediaPlayer;", "setDataSource", "V", ("Ljava/lang/String;",)), [0, 1]),
            invoke(0x6E, 1, mid("Landroid/media/MediaPlayer;", "prepare", "V"), [0]),
            invoke(0x6E, 1, mid("Landroid/media/MediaPlayer;", "start", "V"), [0]),
            invoke(0x6E, 1, mid("Landroid/media/MediaPlayer;", "getDuration", "I"), [0]),
            struct.pack("<H", 0x010A),  # move-result v1
            struct.pack("<HH", 0x11D0, 800),  # add-int/lit16 v1, v1, 800
            struct.pack("<H", 0x1181),  # int-to-long v1, v1
            invoke(0x71, 2, mid("Ljava/lang/Thread;", "sleep", "V", ("J",)), [1, 2]),
            invoke(0x6E, 1, mid("Landroid/media/MediaPlayer;", "release", "V"), [0]),
            struct.pack("<H", 0x000E),  # return-void
        ]
    )
    if len(insns) % 2:
        raise SystemExit("odd insns")

    code_item = struct.pack("<HHHHI", 4, 1, 2, 0, 0) + struct.pack("<I", len(insns) // 2) + insns
    if len(code_item) % 4:
        code_item += b"\0" * (4 - len(code_item) % 4)

    class_data = uleb(0) + uleb(0) + uleb(1) + uleb(0)
    class_data += uleb(mid("LPlay;", "main", "V", ("[Ljava/lang/String;",)))
    class_data += uleb(0x9)  # public static
    # code_off filled after layout

    # Layout
    header_size = 0x70
    string_ids_off = header_size
    string_ids_size = len(strings)
    type_ids_off = string_ids_off + string_ids_size * 4
    type_ids_size = len(types)
    proto_ids_off = type_ids_off + type_ids_size * 4
    proto_ids_size = len(protos)
    # proto_id_item is 12 bytes
    method_ids_off = proto_ids_off + proto_ids_size * 12
    method_ids_size = len(methods)
    class_defs_off = method_ids_off + method_ids_size * 8
    class_defs_size = 1
    data_off = class_defs_off + class_defs_size * 32
    if data_off % 4:
        data_off += 4 - data_off % 4

    # Data: string datas, type lists for protos that have args, proto params, code, class data, map
    blob = bytearray()

    def align(n: int = 4) -> None:
        while (data_off + len(blob)) % n:
            blob.append(0)

    string_offs = []
    for text in strings:
        string_offs.append(data_off + len(blob))
        blob.extend(mutf8(text))

    # type lists for protos with arguments. Each type_list is 4-aligned.
    param_offs = {}
    for ret, args in protos:
        if not args:
            param_offs[(ret, args)] = 0
            continue
        align(4)
        param_offs[(ret, args)] = data_off + len(blob)
        blob.extend(struct.pack("<I", len(args)))
        for arg in args:
            blob.extend(struct.pack("<H", tid(arg)))
        if len(args) % 2:
            blob.extend(b"\0\0")

    align(4)
    code_off = data_off + len(blob)
    blob.extend(code_item)

    class_data = uleb(0) + uleb(0) + uleb(1) + uleb(0)
    class_data += uleb(mid("LPlay;", "main", "V", ("[Ljava/lang/String;",)))
    class_data += uleb(0x9)
    class_data += uleb(code_off)
    class_data_off = data_off + len(blob)
    blob.extend(class_data)

    align(4)
    map_off = data_off + len(blob)
    # map items: header, string_id, type_id, proto_id, method_id, class_def, code, string_data, type_list?, class_data, map_list
    type_list_offs = sorted(off for off in param_offs.values() if off)
    map_items = [
        (0x0000, 1, 0),
        (0x0001, string_ids_size, string_ids_off),
        (0x0002, type_ids_size, type_ids_off),
        (0x0003, proto_ids_size, proto_ids_off),
        (0x0005, method_ids_size, method_ids_off),
        (0x0006, class_defs_size, class_defs_off),
        (0x2005, string_ids_size, string_offs[0] if string_offs else 0),
        (0x2000, len(type_list_offs), type_list_offs[0] if type_list_offs else 0),
        (0x2004, 1, code_off),
        (0x2003, 1, class_data_off),
        (0x1000, 1, map_off),
    ]
    map_items = [item for item in map_items if item[1] > 0]
    map_items = sorted(map_items, key=lambda item: item[2])
    blob.extend(struct.pack("<I", len(map_items)))
    for kind, size, off in map_items:
        blob.extend(struct.pack("<HHII", kind, 0, size, off))

    file_size = data_off + len(blob)
    data_size = file_size - data_off

    out = bytearray(file_size)
    out[0:8] = b"dex\n035\0"
    struct.pack_into("<I", out, 32, file_size)
    struct.pack_into("<I", out, 36, header_size)
    struct.pack_into("<I", out, 40, 0x12345678)
    struct.pack_into("<I", out, 52, map_off)
    struct.pack_into("<I", out, 56, string_ids_size)
    struct.pack_into("<I", out, 60, string_ids_off)
    struct.pack_into("<I", out, 64, type_ids_size)
    struct.pack_into("<I", out, 68, type_ids_off)
    struct.pack_into("<I", out, 72, proto_ids_size)
    struct.pack_into("<I", out, 76, proto_ids_off)
    struct.pack_into("<I", out, 88, method_ids_size)
    struct.pack_into("<I", out, 92, method_ids_off)
    struct.pack_into("<I", out, 96, class_defs_size)
    struct.pack_into("<I", out, 100, class_defs_off)
    struct.pack_into("<I", out, 104, data_size)
    struct.pack_into("<I", out, 108, data_off)
    out[data_off:] = blob

    for index, off in enumerate(string_offs):
        struct.pack_into("<I", out, string_ids_off + index * 4, off)
    for index, descriptor in enumerate(types):
        struct.pack_into("<I", out, type_ids_off + index * 4, sid(descriptor))
    for index, (ret, args) in enumerate(protos):
        base = proto_ids_off + index * 12
        struct.pack_into("<I", out, base, sid(shorty(ret, args)))
        struct.pack_into("<I", out, base + 4, tid(ret))
        struct.pack_into("<I", out, base + 8, param_offs[(ret, args)])
    for index, (owner, name, (ret, args)) in enumerate(methods):
        base = method_ids_off + index * 8
        struct.pack_into("<H", out, base, tid(owner))
        struct.pack_into("<H", out, base + 2, pid(ret, args))
        struct.pack_into("<I", out, base + 4, sid(name))

    # class_def
    base = class_defs_off
    struct.pack_into("<I", out, base, tid("LPlay;"))
    struct.pack_into("<I", out, base + 4, 1)  # public
    struct.pack_into("<I", out, base + 8, tid("Ljava/lang/Object;"))
    struct.pack_into("<I", out, base + 16, 0xFFFFFFFF)  # no source file
    struct.pack_into("<I", out, base + 24, class_data_off)

    digest = hashlib.sha1(out[32:]).digest()
    out[12:32] = digest
    struct.pack_into("<I", out, 8, zlib.adler32(out[12:]) & 0xFFFFFFFF)
    return bytes(out)


def shorty(ret: str, args: tuple[str, ...]) -> str:
    def one(descriptor: str) -> str:
        if descriptor.startswith("L") or descriptor.startswith("["):
            return "L"
        return descriptor

    return one(ret) + "".join(one(arg) for arg in args)


if __name__ == "__main__":
    target = Path(__file__).resolve().parents[1] / "station" / "play.dex"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(build_play_dex("/data/local/tmp/alice-ready.wav"))
    print(target, target.stat().st_size)
