"""把 icon/png/icon-<size>.png 组装成多帧 Windows .ico（PNG 帧，Vista+ 标准做法）。

用法:  python build_ico.py [输出路径] [尺寸,逗号分隔]
默认:  输出 icon/app.ico，帧 16,20,24,32,40,48,64,128,256
"""
import os
import struct
import sys

base = os.path.dirname(os.path.abspath(__file__))
png_dir = os.path.join(base, "png")

out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(base, "app.ico")
sizes = [int(x) for x in (sys.argv[2].split(",") if len(sys.argv) > 2
                          else "16,20,24,32,40,48,64,128,256".split(","))]

frames = []
for s in sizes:
    path = os.path.join(png_dir, "icon-%d.png" % s)
    if not os.path.exists(path):
        sys.exit("缺少渲染结果: %s" % path)
    with open(path, "rb") as f:
        blob = f.read()
    if blob[:8] != b"\x89PNG\r\n\x1a\n":
        sys.exit("不是 PNG: %s" % path)
    frames.append((s, blob))

# ICONDIR: reserved=0, type=1(icon), count
header = struct.pack("<HHH", 0, 1, len(frames))
# 每帧 ICONDIRENTRY 16 字节；宽高 256 用 0 表示
offset = 6 + 16 * len(frames)
entries = b""
payload = b""
for s, blob in frames:
    dim = 0 if s >= 256 else s
    entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
    payload += blob
    offset += len(blob)

with open(out, "wb") as f:
    f.write(header + entries + payload)

print("已生成 %s：%d 帧 (%s)，共 %d 字节"
      % (out, len(frames), ",".join(str(s) for s, _ in frames), os.path.getsize(out)))
