#!/usr/bin/env python3
"""从 assets/app-icon.png 生成 MarkdownLite 的两个图标。

只用 Python 标准库（zlib 解 PNG、手写 ICO 容器），不依赖 Pillow。

产出：
  src/MarkdownLite/app.ico       应用图标 —— 整张图的蓝色圆角方块（外圈留白与投影裁掉，方角透明）
  src/MarkdownLite/md-file.ico   .md 文件类型图标 —— 只取图中「中间的白色文档卡片」，保留圆角与
                             右上折角，四周透明；这就是资源管理器里文件名前面那个小图标
  src/MarkdownLite/app-icon.png  同上的蓝色圆角方块，但存成 128px 位图，供 WPF 界面内嵌使用
                             （顶栏左上角 + 「关于」弹窗头部），保证界面里的图标与 exe 图标同源

为什么用区域分割而不是「按比例裁一块」：文档卡片右上角是折角（不是圆角），
按矩形裁会把折角处的蓝色背景一起带进来；改成从卡片内部做连通区域漫填，
再补内部空洞（蓝色 M↓ 字形、灰色横线），拿到的就是卡片真实轮廓。

用法：
    python scripts/make-icon.py            # 生成两个 .ico
    python scripts/make-icon.py --probe    # 只打印识别到的区域/裁切框，不写文件
    python scripts/make-icon.py --preview  # 额外把 256px 预览 PNG 写到 preview/
"""

import math
import os
import struct
import sys
import zlib

SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
PNG_SIZES = {128, 256}          # 大尺寸用 PNG 压缩存进 ICO，省体积（Vista+ 支持）

# 界面内嵌位图尺寸。逻辑显示尺寸只有 14（顶栏）/ 28（弹窗）px，
# 取 128 是为了在高 DPI（200%）下仍有充足像素，缩放交给 WPF 的 HighQuality 模式。
UI_ICON = 128

# 分类阈值。注意这里有两套「蓝」的判定，不能合并：
# 蓝方块下方有一圈**带蓝味的投影**（实测 b-r 约 62），阈值太松会把它算进应用图标，
# 底部多出一条灰蓝色发糊的边；但阈值太紧又会把卡片边缘的抗锯齿像素误判成卡片，
# 让卡片沿着投影「漏」出去。所以两个方向各用各的阈值。
BACKDROP_BLUE = 85              # b - r 超过它 => 蓝色方块本体
CARD_BLUE = 55                  # b - r 不超过它 + 够亮 => 白色文档卡片
LIGHTNESS = 150                 # 平均亮度低于这个值不算「卡片」


# ======================================================================
# PNG 解码（8 位、真彩 RGB/RGBA、非隔行）
# ======================================================================

def read_png(path):
    """读 PNG，返回 (width, height, bytearray RGBA)。"""
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("不是 PNG 文件")

    pos, idat = 8, bytearray()
    width = height = bit_depth = color_type = interlace = None
    while pos < len(data):
        length = int.from_bytes(data[pos:pos + 4], "big")
        tag = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + length]
        if tag == b"IHDR":
            width = int.from_bytes(chunk[0:4], "big")
            height = int.from_bytes(chunk[4:8], "big")
            bit_depth, color_type = chunk[8], chunk[9]
            interlace = chunk[12]
        elif tag == b"IDAT":
            idat += chunk
        elif tag == b"IEND":
            break
        pos += 12 + length

    if bit_depth != 8 or color_type not in (2, 6) or interlace != 0:
        raise ValueError(
            f"只支持 8 位真彩非隔行 PNG（当前 bitDepth={bit_depth} "
            f"colorType={color_type} interlace={interlace}）")

    channels = 3 if color_type == 2 else 4
    raw = zlib.decompress(bytes(idat))
    stride = width * channels

    out = bytearray(width * height * 4)
    previous = bytearray(stride)
    cursor = 0
    for y in range(height):
        filter_type = raw[cursor]
        cursor += 1
        line = bytearray(raw[cursor:cursor + stride])
        cursor += stride

        if filter_type == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif filter_type == 2:
            for i in range(stride):
                line[i] = (line[i] + previous[i]) & 0xFF
        elif filter_type == 3:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((left + previous[i]) >> 1)) & 0xFF
        elif filter_type == 4:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                up = previous[i]
                up_left = previous[i - channels] if i >= channels else 0
                pa, pb, pc = abs(up - up_left), abs(left - up_left), abs(left + up - 2 * up_left)
                predictor = left if (pa <= pb and pa <= pc) else (up if pb <= pc else up_left)
                line[i] = (line[i] + predictor) & 0xFF
        elif filter_type != 0:
            raise ValueError(f"未知的 PNG 滤波类型 {filter_type}")

        base = y * width * 4
        for x in range(width):
            s = x * channels
            d = base + x * 4
            out[d] = line[s]
            out[d + 1] = line[s + 1]
            out[d + 2] = line[s + 2]
            out[d + 3] = line[s + 3] if channels == 4 else 255
        previous = line

    return width, height, out


def write_png(path, width, height, rgba):
    """把 RGBA（自上而下）写成 PNG，仅用于人工核对预览。"""
    raw = bytearray()
    stride = width * 4
    for y in range(height):
        raw.append(0)
        raw += rgba[y * stride:(y + 1) * stride]

    def chunk(tag, payload):
        return (struct.pack(">I", len(payload)) + tag + payload
                + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(bytes(raw), 6))
           + chunk(b"IEND", b""))
    open(path, "wb").write(png)


# ======================================================================
# 区域分割
# ======================================================================

def is_backdrop_blue(pixel):
    """蓝色方块本体：蓝色通道明显高于红色（阈值偏紧，避开方块下方的蓝味投影）。"""
    r, g, b = pixel[0], pixel[1], pixel[2]
    return b - r > BACKDROP_BLUE


def is_card(pixel):
    """白色文档卡片：够亮、且不带蓝（这样卡片下方的投影不会被误吸进来）。"""
    r, g, b = pixel[0], pixel[1], pixel[2]
    return (b - r) <= CARD_BLUE and (r + g + b) / 3 >= LIGHTNESS


def flood(width, height, rgba, seeds, accept):
    """从 seeds 出发做 4 邻域漫填，返回 bytearray 掩码（1 = 命中）。"""
    mask = bytearray(width * height)
    stack = []
    for x, y in seeds:
        index = y * width + x
        if not mask[index] and accept(rgba[index * 4:index * 4 + 3]):
            mask[index] = 1
            stack.append((x, y))

    while stack:
        x, y = stack.pop()
        for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
            if nx < 0 or ny < 0 or nx >= width or ny >= height:
                continue
            index = ny * width + nx
            if mask[index]:
                continue
            if not accept(rgba[index * 4:index * 4 + 3]):
                continue
            mask[index] = 1
            stack.append((nx, ny))
    return mask


def outer_region(width, height, rgba):
    """图片边框外的区域（白色留白 + 蓝方块下方的投影），被蓝色方块挡住不会漏进去。"""
    seeds = [(x, 0) for x in range(0, width, 8)] + [(x, height - 1) for x in range(0, width, 8)]
    seeds += [(0, y) for y in range(0, height, 8)] + [(width - 1, y) for y in range(0, height, 8)]
    return flood(width, height, rgba, seeds, lambda p: not is_backdrop_blue(p))


def card_region(width, height, rgba):
    """文档卡片内部（含被卡片包住的蓝色 M 字形与灰色横线这些内部空洞）。"""
    # 种子点取图片正中偏上：一定落在卡片的白底上（不会是字形、横线或蓝色背景）
    seed = (width // 2, int(height * 0.45))
    inner = flood(width, height, rgba, [seed], is_card)

    # 补空洞：从四边出发，把「不属于卡片、且与图片边框连通」的区域标出来，
    # 剩下没被标记的像素就是卡片内部被包住的字形/横线（或折角三角），一并并入卡片。
    seeds = [(x, 0) for x in range(0, width, 4)] + [(x, height - 1) for x in range(0, width, 4)]
    seeds += [(0, y) for y in range(0, height, 4)] + [(width - 1, y) for y in range(0, height, 4)]

    reachable = bytearray(width * height)
    stack = []
    for x, y in seeds:
        index = y * width + x
        if inner[index] or reachable[index]:
            continue
        reachable[index] = 1
        stack.append((x, y))
    while stack:
        x, y = stack.pop()
        for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
            if nx < 0 or ny < 0 or nx >= width or ny >= height:
                continue
            index = ny * width + nx
            if inner[index] or reachable[index]:
                continue
            reachable[index] = 1
            stack.append((nx, ny))

    return bytearray(0 if reachable[i] else 1 for i in range(width * height))


def mask_bounds(mask, width, height):
    """掩码的非空包围盒 (left, top, right, bottom)，右下为开区间。"""
    left, top, right, bottom = width, height, 0, 0
    for y in range(height):
        row = y * width
        for x in range(width):
            if mask[row + x]:
                if x < left:
                    left = x
                if x >= right:
                    right = x + 1
                if y < top:
                    top = y
                if y >= bottom:
                    bottom = y + 1
    if right <= left or bottom <= top:
        raise ValueError("掩码为空，图片可能不符合预期")
    return left, top, right, bottom


def smooth(mask, width, height, passes=2):
    """对二值掩码做几轮 3x3 均值（可分离实现），得到抗锯齿的柔和边缘。"""
    current = [float(v) for v in mask]
    for _ in range(passes):
        # 横向
        horizontal = [0.0] * len(current)
        for y in range(height):
            row = y * width
            for x in range(width):
                total = current[row + x]
                count = 1
                if x > 0:
                    total += current[row + x - 1]
                    count += 1
                if x < width - 1:
                    total += current[row + x + 1]
                    count += 1
                horizontal[row + x] = total / count
        # 纵向
        vertical = [0.0] * len(horizontal)
        for y in range(height):
            row = y * width
            for x in range(width):
                total = horizontal[row + x]
                count = 1
                if y > 0:
                    total += horizontal[row - width + x]
                    count += 1
                if y < height - 1:
                    total += horizontal[row + width + x]
                    count += 1
                vertical[row + x] = total / count
        current = vertical
    return current


def extract(rgba, width, height, mask, box, canvas_side=None, pad_ratio=0.0):
    """把 mask 覆盖的区域裁出来，放到正方形画布中央，返回 (side, RGBA bytes)。"""
    left, top, right, bottom = box
    content_w, content_h = right - left, bottom - top

    if canvas_side is None:
        canvas_side = round(max(content_w, content_h) * (1.0 + pad_ratio * 2))
    offset_x = (canvas_side - content_w) // 2
    offset_y = (canvas_side - content_h) // 2

    out = bytearray(canvas_side * canvas_side * 4)
    for y in range(content_h):
        source_y = top + y
        for x in range(content_w):
            source_x = left + x
            alpha = mask[source_y * width + source_x]
            if alpha <= 0.0:
                continue
            s = (source_y * width + source_x) * 4
            d = ((y + offset_y) * canvas_side + (x + offset_x)) * 4
            out[d] = rgba[s]
            out[d + 1] = rgba[s + 1]
            out[d + 2] = rgba[s + 2]
            out[d + 3] = min(255, round(alpha * 255))
    return canvas_side, out


# ======================================================================
# 缩放与 ICO 输出
# ======================================================================

def resize_area(rgba, size, target):
    """面积平均缩放，RGBA 先预乘再还原，避免透明边缘出现深色描边。"""
    if size == target:
        return bytes(rgba)

    out = bytearray(target * target * 4)
    scale = size / target
    for dy in range(target):
        sy0, sy1 = dy * scale, (dy + 1) * scale
        iy0, iy1 = int(sy0), min(size, int(math.ceil(sy1)))
        for dx in range(target):
            sx0, sx1 = dx * scale, (dx + 1) * scale
            ix0, ix1 = int(sx0), min(size, int(math.ceil(sx1)))

            acc = [0.0, 0.0, 0.0, 0.0]
            weight_sum = 0.0
            for sy in range(iy0, iy1):
                wy = min(sy + 1.0, sy1) - max(float(sy), sy0)
                if wy <= 0:
                    continue
                for sx in range(ix0, ix1):
                    wx = min(sx + 1.0, sx1) - max(float(sx), sx0)
                    if wx <= 0:
                        continue
                    w = wx * wy
                    i = (sy * size + sx) * 4
                    alpha = rgba[i + 3] / 255.0
                    acc[0] += rgba[i] * alpha * w
                    acc[1] += rgba[i + 1] * alpha * w
                    acc[2] += rgba[i + 2] * alpha * w
                    acc[3] += rgba[i + 3] * w
                    weight_sum += w

            o = (dy * target + dx) * 4
            if weight_sum <= 0 or acc[3] <= 0:
                continue
            alpha = acc[3] / weight_sum
            out[o] = min(255, round(acc[0] / weight_sum / (alpha / 255.0)))
            out[o + 1] = min(255, round(acc[1] / weight_sum / (alpha / 255.0)))
            out[o + 2] = min(255, round(acc[2] / weight_sum / (alpha / 255.0)))
            out[o + 3] = min(255, round(alpha))
    return bytes(out)


def dib_image(size, rgba):
    """ICO 内嵌的 DIB：BITMAPINFOHEADER + 自下而上 BGRA + AND 掩码。"""
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    xor = bytearray()
    for y in range(size - 1, -1, -1):
        for x in range(size):
            i = (y * size + x) * 4
            xor += bytes((rgba[i + 2], rgba[i + 1], rgba[i], rgba[i + 3]))

    mask_row_bytes = ((size + 31) // 32) * 4
    mask = bytearray()
    for y in range(size - 1, -1, -1):
        bits = bytearray(mask_row_bytes)
        for x in range(size):
            if rgba[(y * size + x) * 4 + 3] < 128:
                bits[x // 8] |= 0x80 >> (x % 8)
        mask += bits
    return header + bytes(xor) + bytes(mask)


def png_bytes(size, rgba):
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        raw += rgba[y * size * 4:(y + 1) * size * 4]

    def chunk(tag, payload):
        return (struct.pack(">I", len(payload)) + tag + payload
                + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


def write_ico(path, size, rgba):
    images = []
    for s in SIZES:
        scaled = resize_area(rgba, size, s)
        images.append(png_bytes(s, scaled) if s in PNG_SIZES else dib_image(s, scaled))

    directory = struct.pack("<HHH", 0, 1, len(SIZES))
    offset = len(directory) + 16 * len(SIZES)
    entries = bytearray()
    for s, data in zip(SIZES, images):
        entries += struct.pack("<BBBBHHII", s if s < 256 else 0, s if s < 256 else 0,
                               0, 0, 1, 32, len(data), offset)
        offset += len(data)

    with open(path, "wb") as handle:
        handle.write(directory)
        handle.write(bytes(entries))
        for data in images:
            handle.write(data)
    return os.path.getsize(path)


# ======================================================================

def build(source):
    """返回 (app_rgba, app_side, md_rgba, md_side, 诊断信息)。"""
    width, height, rgba = read_png(source)

    # 1) 应用图标：蓝色方块本体（外圈留白与投影都在 outer 里，直接排除）
    outer = outer_region(width, height, rgba)
    app_mask = [0.0 if outer[i] else 1.0 for i in range(width * height)]
    app_box = mask_bounds(bytearray(1 if v > 0 else 0 for v in app_mask), width, height)
    app_mask = smooth(app_mask, width, height, passes=2)
    app_side, app_rgba = extract(rgba, width, height, app_mask, app_box)

    # 2) .md 文件图标：中间的白色文档卡片
    card = card_region(width, height, rgba)
    card_box = mask_bounds(card, width, height)
    card_soft = smooth([float(v) for v in card], width, height, passes=2)
    md_side, md_rgba = extract(rgba, width, height, card_soft, card_box, pad_ratio=0.03)

    info = (f"源图 {width}x{height}\n"
            f"应用图标：蓝方块包围盒 {app_box}（{app_box[2] - app_box[0]}x{app_box[3] - app_box[1]}）"
            f" -> 画布 {app_side}\n"
            f"md 图标：卡片包围盒 {card_box}（{card_box[2] - card_box[0]}x{card_box[3] - card_box[1]}）"
            f" -> 画布 {md_side}")
    return app_side, app_rgba, md_side, md_rgba, info


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    source = os.path.join(root, "assets", "app-icon.png")
    app_out = os.path.join(root, "src", "MarkdownLite", "app.ico")
    md_out = os.path.join(root, "src", "MarkdownLite", "md-file.ico")
    ui_out = os.path.join(root, "src", "MarkdownLite", "app-icon.png")

    app_side, app_rgba, md_side, md_rgba, info = build(source)
    print(info)

    if "--probe" in sys.argv:
        return 0

    if "--preview" in sys.argv:
        preview_dir = os.path.join(root, "preview")
        os.makedirs(preview_dir, exist_ok=True)
        for side, data, name in ((app_side, app_rgba, "图标-应用-预览"),
                                 (md_side, md_rgba, "图标-md文件-预览")):
            target = 256
            write_png(os.path.join(preview_dir, name + ".png"), target, target,
                      resize_area(data, side, target))
        print("预览 PNG 已写入 preview/")

    app_size = write_ico(app_out, app_side, app_rgba)
    md_size = write_ico(md_out, md_side, md_rgba)
    print(f"已生成 {app_out}（{len(SIZES)} 个尺寸，{app_size / 1024:.1f} KB）")
    print(f"已生成 {md_out}（{len(SIZES)} 个尺寸，{md_size / 1024:.1f} KB）")

    write_png(ui_out, UI_ICON, UI_ICON, resize_area(app_rgba, app_side, UI_ICON))
    print(f"已生成 {ui_out}（{UI_ICON}x{UI_ICON}，{os.path.getsize(ui_out) / 1024:.1f} KB）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
