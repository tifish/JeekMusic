"""生成 Windows 图标 src/JeekMusic.App/Assets/JeekMusic.ico 和 PNG（和移动端的启动图标一样：靛蓝圆角底、白色八分音符）。

图形取自 mobile/app/src/main/res/drawable/ic_launcher_foreground.xml（108×108 的画布）。需要 Pillow。
"""
from pathlib import Path
from PIL import Image, ImageDraw

out = Path(__file__).resolve().parent.parent / "src" / "JeekMusic.App" / "Assets"
S = 1024  # 先画大图再缩小，边缘更平滑
BG = (0x3F, 0x51, 0xB5, 255)


def draw(size_px: int) -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, S - 1, S - 1], radius=S * 0.22, fill=BG)
    # 移动端的前景在 108 的画布上，可见区域约是中间的 72（自适应图标的安全区）；这里把 18..90 映射到整张图
    k = S / 72
    def p(x, y): return ((x - 18) * k, (y - 18) * k)
    # 符头：中心 (45, 70)，半径 9×8，稍微倾斜的椭圆用多边形近似
    import math
    pts = []
    for i in range(72):
        a = 2 * math.pi * i / 72
        x, y = 9 * math.cos(a), 8 * math.sin(a)
        r = math.radians(-20)
        pts.append(p(45 + x * math.cos(r) - y * math.sin(r), 70 + x * math.sin(r) + y * math.cos(r)))
    d.polygon(pts, fill="white")
    d.rectangle([*p(50, 36), *p(54, 70)], fill="white")  # 符干
    # 符尾：三次贝塞尔 M54,36 C58,42 70,44 68,56 C66,50 60,47 54,46 z
    def bez(p0, p1, p2, p3, n=40):
        return [(((1-t)**3)*p0[0] + 3*((1-t)**2)*t*p1[0] + 3*(1-t)*t*t*p2[0] + t**3*p3[0],
                 ((1-t)**3)*p0[1] + 3*((1-t)**2)*t*p1[1] + 3*(1-t)*t*t*p2[1] + t**3*p3[1]) for t in [i / n for i in range(n + 1)]]
    flag = bez((54, 36), (58, 42), (70, 44), (68, 56)) + bez((68, 56), (66, 50), (60, 47), (54, 46))
    d.polygon([p(x, y) for x, y in flag], fill="white")
    return img.resize((size_px, size_px), Image.LANCZOS)


big = draw(256)
big.save(out / "JeekMusic.png")
big.save(out / "JeekMusic.ico", sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
print("written", out)


# ── 任务栏缩略图按钮（上一首 / 播放 / 暂停 / 下一首），白色图形，透明底 ──
def glyph(kind: str, size: int = 64) -> Image.Image:
    s = 512
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    w = (255, 255, 255, 255)
    if kind == "play":
        d.polygon([(150, 100), (150, 412), (410, 256)], fill=w)
    elif kind == "pause":
        d.rounded_rectangle([140, 100, 225, 412], radius=24, fill=w)
        d.rounded_rectangle([287, 100, 372, 412], radius=24, fill=w)
    elif kind == "next":
        d.polygon([(110, 110), (110, 402), (330, 256)], fill=w)
        d.rounded_rectangle([340, 110, 400, 402], radius=18, fill=w)
    elif kind == "previous":
        d.polygon([(402, 110), (402, 402), (182, 256)], fill=w)
        d.rounded_rectangle([112, 110, 172, 402], radius=18, fill=w)
    return img.resize((size, size), Image.LANCZOS)


tb = out / "Taskbar"
tb.mkdir(exist_ok=True)
for kind in ["play", "pause", "next", "previous"]:
    glyph(kind).save(tb / f"{kind}.png")
print("taskbar glyphs written")
