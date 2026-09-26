#!/usr/bin/env python3
"""HerdMe brag video renderer. Every frame is a pure function of time t."""
import math, sys, os, functools
import numpy as np
import cairo
from PIL import Image, ImageFont, ImageDraw

W, H, FPS = 1920, 1080, 30
BPM = 112.0
BEAT = 60.0 / BPM
DUR = 42 * BEAT  # 22.5s
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))

# scene boundaries on the beat grid
S1, S2, S3, S4, S5, S6 = 0.0, 5 * BEAT, 11 * BEAT, 20 * BEAT, 27 * BEAT, 33 * BEAT

# ---------- palette (Styles/DesignSystem.xaml, light theme) ----------
def hx(s, a=1.0):
    s = s.lstrip("#")
    return (int(s[0:2], 16) / 255, int(s[2:4], 16) / 255, int(s[4:6], 16) / 255, a)

BRAND = hx("C92B3D"); ICONRED = hx("ED1F2B"); INK = hx("202124"); WHITE = hx("FFFFFF")
SHELL = hx("F4F5F7"); SUBTLE = hx("F7F8FA"); STROKE = hx("E3E4E8"); SECOND = hx("5F6368")
SITES_T, SITES_I = hx("FCEDEF"), hx("C92B3D")
SERV_T, SERV_I = hx("E7F5F0"), hx("13765D")
MAIL_T, MAIL_I = hx("EAF1FC"), hx("315EAD")
DUMP_T, DUMP_I = hx("FFF3DB"), hx("956300")
OK_GREEN = hx("0F7B0F")
DARKBRAND = hx("FF8794")

F = {
    "pop_b": "/usr/share/fonts/truetype/google-fonts/Poppins-Bold.ttf",
    "pop_m": "/usr/share/fonts/truetype/google-fonts/Poppins-Medium.ttf",
    "pop_r": "/usr/share/fonts/truetype/google-fonts/Poppins-Regular.ttf",
    "ui": "/usr/share/fonts/truetype/lato/Lato-Regular.ttf",
    "ui_sb": "/usr/share/fonts/truetype/lato/Lato-Semibold.ttf",
    "ui_b": "/usr/share/fonts/truetype/lato/Lato-Bold.ttf",
    "mono": "/usr/share/fonts/truetype/noto/NotoSansMono-Regular.ttf",
    "ar": "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
    "ar_b": "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
    "fa": "/usr/share/fonts/truetype/font-awesome/fontawesome-webfont.ttf",
}
FA = dict(home="\uf015", folder="\uf07b", code="\uf121", apps="\uf009", mail="\uf0e0", eye="\uf06e",
          bug="\uf188", term="\uf120", list="\uf03a", cog="\uf013", lock="\uf023", check="\uf00c",
          plus="\uf067", globe="\uf0ac", search="\uf002", db="\uf1c0", server="\uf233", down="\uf019",
          pencil="\uf040", more="\uf141", share="\uf1e0", heart="\uf21e", sitemap="\uf0e8", open="\uf07c",
          copy="\uf0c5", leaf="\uf06c", cube="\uf1b2", bolt="\uf0e7", refresh="\uf021", star="\uf005", play="\uf04b")

# ---------- easing ----------
def clamp(x, a=0.0, b=1.0): return max(a, min(b, x))
def prog(t, a, b): return clamp((t - a) / (b - a)) if b > a else float(t >= a)
def eo3(x): return 1 - (1 - x) ** 3
def eio(x): return 4 * x ** 3 if x < 0.5 else 1 - (-2 * x + 2) ** 3 / 2
def eback(x, s=1.9):
    x -= 1
    return x * x * ((s + 1) * x + s) + 1
def lerp(a, b, x): return a + (b - a) * x
def mix(c1, c2, x): return tuple(lerp(c1[i], c2[i], x) for i in range(4))

# ---------- text via PIL/raqm, cached as cairo surfaces ----------
@functools.lru_cache(maxsize=4096)
def _font(path, px):
    return ImageFont.truetype(path, px, layout_engine=ImageFont.Layout.RAQM)

@functools.lru_cache(maxsize=4096)
def text_surf(s, font, px, color, rtl=False):
    f = _font(F[font], px)
    kw = dict(direction="rtl", language="ar") if rtl else {}
    x0, y0, x1, y1 = f.getbbox(s, anchor="ls", **kw)
    pad = 4
    w, h = max(1, x1 - x0 + 2 * pad), max(1, y1 - y0 + 2 * pad)
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    col = tuple(int(round(c * 255)) for c in color[:3]) + (255,)
    d.text((pad - x0, pad - y0), s, font=f, fill=col, anchor="ls", **kw)
    a = np.asarray(img).astype(np.float32)
    al = a[..., 3:4] / 255.0
    pm = np.concatenate([a[..., 2:3] * al, a[..., 1:2] * al, a[..., 0:1] * al, a[..., 3:4]], axis=2)
    stride = cairo.ImageSurface.format_stride_for_width(cairo.FORMAT_ARGB32, w)
    buf = np.zeros((h, stride // 4, 4), np.uint8)
    buf[:, :w, :] = np.clip(pm + 0.5, 0, 255).astype(np.uint8)
    surf = cairo.ImageSurface.create_for_data(bytearray(buf.tobytes()), cairo.FORMAT_ARGB32, w, h, stride)
    adv = f.getlength(s, **kw)
    return surf, x0 - pad, y0 - pad, adv

def text_w(s, font, size, ds=1.0, rtl=False):
    return text_surf(s, font, max(1, int(round(size * ds))), (0, 0, 0, 1), rtl)[3] / ds

def text(ctx, s, x, y, font="ui", size=14, color=INK, anchor="l", alpha=1.0, ds=1.0, rtl=False,
         scale=1.0, pivot=None):
    """Draw text with baseline at (x,y). ds = current device scale (for crisp rendering)."""
    if alpha <= 0.001 or not s: return 0
    px = max(1, int(round(size * ds * scale)))
    surf, ox, oy, adv = text_surf(s, font, px, tuple(color), rtl)
    k = 1.0 / ds / (px / (size * ds * scale)) if size * ds * scale else 1.0  # px rounding compensation
    k = (size * scale) / px  # logical units per rendered pixel
    width = adv * k
    fx = {"l": 0.0, "c": 0.5, "r": 1.0}[anchor]
    ctx.save()
    ctx.translate(x - width * fx, y)
    ctx.scale(k, k)
    ctx.set_source_surface(surf, ox, oy)
    ctx.paint_with_alpha(clamp(alpha * color[3]))
    ctx.restore()
    return width

def icon(ctx, name, x, y, size, color, alpha=1.0, ds=1.0, anchor="c"):
    """FontAwesome glyph centred on (x,y)."""
    g = FA[name]
    px = max(1, int(round(size * ds)))
    surf, ox, oy, adv = text_surf(g, "fa", px, tuple(color))
    k = size / px
    f = _font(F["fa"], px)
    bx0, by0, bx1, by1 = f.getbbox(g, anchor="ls")
    cx = (bx0 + bx1) / 2 * k; cy = (by0 + by1) / 2 * k
    ctx.save(); ctx.translate(x - cx, y - cy); ctx.scale(k, k)
    ctx.set_source_surface(surf, ox, oy); ctx.paint_with_alpha(alpha * color[3]); ctx.restore()

# ---------- shapes ----------
def rrect(ctx, x, y, w, h, r):
    r = min(r, w / 2, h / 2)
    ctx.new_sub_path()
    ctx.arc(x + w - r, y + r, r, -math.pi / 2, 0)
    ctx.arc(x + w - r, y + h - r, r, 0, math.pi / 2)
    ctx.arc(x + r, y + h - r, r, math.pi / 2, math.pi)
    ctx.arc(x + r, y + r, r, math.pi, 1.5 * math.pi)
    ctx.close_path()

def fill_rr(ctx, x, y, w, h, r, color, alpha=1.0):
    rrect(ctx, x, y, w, h, r)
    ctx.set_source_rgba(color[0], color[1], color[2], color[3] * alpha); ctx.fill()

def stroke_rr(ctx, x, y, w, h, r, color, lw=1.0, alpha=1.0):
    rrect(ctx, x + lw / 2, y + lw / 2, w - lw, h - lw, r)
    ctx.set_source_rgba(color[0], color[1], color[2], color[3] * alpha); ctx.set_line_width(lw); ctx.stroke()

def shadow(ctx, x, y, w, h, r, strength=1.0, spread=28, dy=14):
    for i in range(10, 0, -1):
        e = spread * i / 10
        rrect(ctx, x - e, y - e + dy * (1 - i / 14), w + 2 * e, h + 2 * e, r + e)
        ctx.set_source_rgba(0.1, 0.1, 0.15, 0.022 * strength)
        ctx.fill()

def circle(ctx, x, y, r, color, alpha=1.0):
    ctx.arc(x, y, r, 0, 2 * math.pi)
    ctx.set_source_rgba(color[0], color[1], color[2], color[3] * alpha); ctx.fill()

@functools.lru_cache(maxsize=4)
def app_icon(px):
    p = os.path.join(REPO, "HerdMe/Resources/Assets.xcassets/AppIcon.appiconset/icon-1024.png")
    img = Image.open(p).convert("RGBA").resize((px, px), Image.LANCZOS)
    a = np.asarray(img).astype(np.float32); al = a[..., 3:4] / 255
    pm = np.concatenate([a[..., 2:3] * al, a[..., 1:2] * al, a[..., 0:1] * al, a[..., 3:4]], axis=2)
    stride = cairo.ImageSurface.format_stride_for_width(cairo.FORMAT_ARGB32, px)
    buf = np.zeros((px, stride // 4, 4), np.uint8); buf[:, :px] = np.clip(pm + .5, 0, 255).astype(np.uint8)
    return cairo.ImageSurface.create_for_data(bytearray(buf.tobytes()), cairo.FORMAT_ARGB32, px, px, stride)

def draw_icon(ctx, cx, cy, size, alpha=1.0, long_shadow=0.0, sh_len=1.4, sh_max=0.55):
    if long_shadow > 0:  # 2016 flat long shadow, drawn as one flat shape with a fade
        ctx.save()
        L = size * sh_len * long_shadow
        n = max(1, int(L / 3))
        a = 1 - (1 - sh_max) ** (1 / n)
        r = size * 0.19
        for i in range(0, n):
            rrect(ctx, cx - size / 2 + i * 3, cy - size / 2 + i * 3, size, size, r)
            ctx.set_source_rgba(0, 0, 0, a * alpha * (1 - i / n) ** 0.35)
            ctx.fill()
        ctx.restore()
    px = 512 if size > 256 else 256
    s = app_icon(px)
    ctx.save(); ctx.translate(cx - size / 2, cy - size / 2); ctx.scale(size / px, size / px)
    ctx.set_source_surface(s, 0, 0); ctx.paint_with_alpha(alpha); ctx.restore()

def cursor(ctx, x, y, press=0.0, alpha=1.0):
    ctx.save(); ctx.translate(x, y); s = 1.35 * (1 - 0.12 * press); ctx.scale(s, s)
    pts = [(0, 0), (0, 22), (5.5, 17), (9.5, 25.5), (13, 24), (9, 15.8), (16.2, 15.8)]
    for i, (px, py) in enumerate(pts):
        (ctx.move_to if i == 0 else ctx.line_to)(px, py)
    ctx.close_path()
    ctx.set_source_rgba(1, 1, 1, alpha); ctx.fill_preserve()
    ctx.set_source_rgba(0, 0, 0, alpha); ctx.set_line_width(1.3); ctx.set_line_join(cairo.LINE_JOIN_ROUND); ctx.stroke()
    ctx.restore()

# 2016 flat confetti: deterministic shapes that drift gently
RNG = np.random.default_rng(2016)
CONF = [dict(x=RNG.uniform(60, W - 60), y=RNG.uniform(60, H - 60), k=RNG.integers(0, 4), s=RNG.uniform(14, 34),
             r=RNG.uniform(0, 6.28), v=RNG.uniform(-0.4, 0.4), c=RNG.integers(0, 4)) for _ in range(26)]
CONF_COLS = [SITES_T, SERV_T, MAIL_T, DUMP_T]

def confetti(ctx, t, alpha=1.0, avoid=None, cols=None):
    cols = cols or CONF_COLS
    for c in CONF:
        x = c["x"] + 18 * math.sin(t * 0.7 + c["r"]); y = c["y"] + 14 * math.cos(t * 0.6 + c["r"] * 1.3)
        if avoid and avoid[0] < x < avoid[2] and avoid[1] < y < avoid[3]: continue
        col = cols[c["c"]]
        ctx.save(); ctx.translate(x, y); ctx.rotate(c["r"] + t * c["v"])
        s = c["s"]
        ctx.set_source_rgba(col[0], col[1], col[2], alpha)
        if c["k"] == 0: ctx.arc(0, 0, s / 2, 0, 6.2832); ctx.fill()
        elif c["k"] == 1:
            ctx.move_to(0, -s / 2); ctx.line_to(s / 2, s / 2); ctx.line_to(-s / 2, s / 2); ctx.close_path(); ctx.fill()
        elif c["k"] == 2: ctx.arc(0, 0, s / 2, 0, 6.2832); ctx.set_line_width(5); ctx.stroke()
        else:
            ctx.rectangle(-s / 2, -3, s, 6); ctx.fill(); ctx.rectangle(-3, -s / 2, 6, s); ctx.fill()
        ctx.restore()

def bg(ctx, color):
    ctx.set_source_rgba(*color); ctx.paint()

# =====================================================================
# Scene 1: "We're disrupting localhost."
# =====================================================================
def scene1(ctx, t):
    bg(ctx, WHITE)
    confetti(ctx, t, alpha=eo3(prog(t, 0.9, 1.6)) * 0.9, avoid=(260, 330, 1660, 760))
    l1 = "We're disrupting"
    n = int(round(len(l1) * prog(t, 0.08, 0.85)))
    shown = l1[:n]
    y1, y2 = 470, 680
    fullw = text_w(l1, "pop_b", 128)
    x0 = W / 2 - fullw / 2
    text(ctx, shown, x0, y1, "pop_b", 128, INK)
    slam = 2 * BEAT
    if t < slam:
        wx = x0 + text_w(shown, "pop_b", 128) + 8
        if (t * 2.2) % 1 < 0.6 or t < 0.85:
            ctx.rectangle(wx, y1 - 104, 12, 120); ctx.set_source_rgba(*BRAND); ctx.fill()
    else:
        p = prog(t, slam, slam + 0.28)
        sc = lerp(1.55, 1.0, eback(p, 1.4)) if p < 1 else 1.0
        text(ctx, "localhost.", W / 2, y2, "pop_b", 190, BRAND, anchor="c", scale=sc, alpha=clamp(p * 4))
        # underline swipe
        u = eo3(prog(t, slam + 0.18, slam + 0.55))
        uw = text_w("localhost.", "pop_b", 190) * 0.92
        ctx.rectangle(W / 2 - uw / 2, y2 + 34, uw * u, 14); ctx.set_source_rgba(*INK); ctx.fill()

# =====================================================================
# Scene 2: reveal
# =====================================================================
def scene2(ctx, t):
    bg(ctx, WHITE)
    wipe = eio(prog(t, 0, 0.3))
    ctx.rectangle(0, H * (1 - wipe), W, H * wipe + 1); ctx.set_source_rgba(*INK); ctx.fill()
    if wipe < 1: return
    confetti(ctx, t + 3, alpha=0.10 * prog(t, 0.3, 0.8), cols=[WHITE] * 4, avoid=(360, 200, 1560, 900))
    p = prog(t, 0.22, 0.72)
    sc = eback(p, 2.2) if p > 0 else 0
    if sc > 0:
        draw_icon(ctx, W / 2, 360, 260 * sc, long_shadow=eo3(prog(t, 0.6, 1.1)))
    q = eo3(prog(t, 0.55, 0.95))
    text(ctx, "HerdMe", W / 2, 640 + 40 * (1 - q), "pop_b", 150, WHITE, anchor="c", alpha=q)
    r = eo3(prog(t, 0.85, 1.25))
    text(ctx, "Local PHP & Laravel. Native on Windows.", W / 2, 760 + 30 * (1 - r), "pop_m", 54, DARKBRAND,
         anchor="c", alpha=r)

# =====================================================================
# Windows app window (rebuilt from MainWindow.xaml + SitesPage.xaml)
# =====================================================================
NAV = [("item", "home", "Dashboard"), ("item", "folder", "Sites"), ("hdr", None, "Environment"),
       ("item", "code", "PHP"), ("item", "leaf", "Node.js"), ("item", "apps", "Services"),
       ("hdr", None, "Tools"), ("item", "mail", "Mail"), ("item", "eye", "Dumps"), ("item", "bug", "Debugger"),
       ("item", "term", "Tinker"), ("item", "list", "Logs")]

def button(ctx, x, y, w, h, label, ds, accent=False, ic=None, press=0.0, hover=0.0):
    if accent:
        col = mix(BRAND, hx("B62435"), hover); col = mix(col, hx("9D2030"), press)
        fill_rr(ctx, x, y, w, h, 4, col); fg = WHITE
    else:
        fill_rr(ctx, x, y, w, h, 4, WHITE); stroke_rr(ctx, x, y, w, h, 4, STROKE); fg = INK
    tw = text_w(label, "ui", 14, ds) + (22 if ic else 0)
    sx = x + w / 2 - tw / 2
    if ic:
        icon(ctx, ic, sx + 7, y + h / 2, 13, fg, ds=ds); sx += 22
    text(ctx, label, sx, y + h / 2 + 5, "ui", 14, fg, ds=ds)

def site_row(ctx, x, y, w, name, sub, state, ds, alpha=1.0, selected=False, creating=0.0):
    if alpha <= 0: return
    ctx.push_group()
    if selected: fill_rr(ctx, x + 4, y, w - 8, 56, 6, hx("EEF0F3"))
    fill_rr(ctx, x + 14, y + 12, 32, 32, 6, SITES_T)
    icon(ctx, "globe", x + 30, y + 28, 16, SITES_I, ds=ds)
    text(ctx, name, x + 58, y + 25, "ui_sb", 14, INK, ds=ds)
    text(ctx, sub, x + 58, y + 44, "ui", 12, SECOND, ds=ds)
    if state == "running": circle(ctx, x + w - 24, y + 28, 5, OK_GREEN)
    elif state == "creating":
        ctx.set_source_rgba(*STROKE); ctx.rectangle(x + w - 74, y + 26, 54, 4); ctx.fill()
        ctx.set_source_rgba(*BRAND); ctx.rectangle(x + w - 74, y + 26, 54 * creating, 4); ctx.fill()
    ctx.pop_group_to_source(); ctx.paint_with_alpha(alpha)

def window(ctx, t, ox, oy, S, st):
    """st: dict(create_press, create_hover, new_row (0..1), new_state, created (0..1))"""
    Wl, Hl = 1280, 720
    ds = S
    shadow(ctx, ox, oy, Wl * S, Hl * S, 10 * S, strength=1.2)
    ctx.save(); ctx.translate(ox, oy); ctx.scale(S, S)
    rrect(ctx, 0, 0, Wl, Hl, 8); ctx.clip()
    g = cairo.LinearGradient(0, 0, Wl, Hl); g.add_color_stop_rgba(0, *hx("F3F1F4")); g.add_color_stop_rgba(1, *hx("EEF1F6"))
    ctx.set_source(g); ctx.paint()
    # title bar
    ctx.save(); ctx.translate(14, 14)
    draw_icon(ctx, 10, 10, 20); ctx.restore()
    text(ctx, "HerdMe", 44, 30, "ui", 13, INK, ds=ds)
    bx = 104; bw = 20 + text_w("Local sites: Running", "ui", 12, ds) + 12
    fill_rr(ctx, bx, 14, bw, 22, 11, hx("DFF1E4")); circle(ctx, bx + 12, 25, 4, OK_GREEN)
    text(ctx, "Local sites: Running", bx + 22, 29.5, "ui", 12, hx("0B5A0B"), ds=ds)
    ctx.set_source_rgba(*INK); ctx.set_line_width(1)
    ctx.move_to(Wl - 128, 24.5); ctx.line_to(Wl - 118, 24.5); ctx.stroke()
    ctx.rectangle(Wl - 82.5, 19.5, 10, 10); ctx.stroke()
    ctx.move_to(Wl - 36, 19.5); ctx.line_to(Wl - 26, 29.5); ctx.move_to(Wl - 26, 19.5); ctx.line_to(Wl - 36, 29.5); ctx.stroke()
    # nav
    y = 58
    for kind, ic, lab in NAV:
        if kind == "hdr":
            y += 8; text(ctx, lab, 18, y + 16, "ui_sb", 12, SECOND, ds=ds); y += 28; continue
        if lab == "Sites":
            fill_rr(ctx, 6, y, 228, 36, 5, hx("E6E7EB", 0.9))
            fill_rr(ctx, 6, y + 10, 3, 16, 1.5, BRAND)
        icon(ctx, ic, 30, y + 18, 14, INK, ds=ds)
        text(ctx, lab, 56, y + 23, "ui", 14, INK, ds=ds)
        y += 38
    icon(ctx, "down", 30, Hl - 76, 14, INK, ds=ds); text(ctx, "Updates", 56, Hl - 71, "ui", 14, INK, ds=ds)
    icon(ctx, "cog", 30, Hl - 36, 14, INK, ds=ds); text(ctx, "General", 56, Hl - 31, "ui", 14, INK, ds=ds)
    # content layer
    cx0, cy0 = 240, 48
    ctx.save(); rrect(ctx, cx0, cy0, Wl - cx0 + 20, Hl - cy0 + 20, 8)
    ctx.set_source_rgba(1, 1, 1, 1); ctx.fill_preserve(); ctx.set_source_rgba(*STROKE); ctx.set_line_width(1); ctx.stroke(); ctx.restore()
    px = cx0 + 28
    text(ctx, "Sites", px, 104, "ui_sb", 28, INK, ds=ds)
    circle(ctx, px + 5, 124, 4.5, OK_GREEN); text(ctx, "Running", px + 16, 129, "ui", 14, SECOND, ds=ds)
    # header buttons (right aligned)
    rx = Wl - 28
    cw = 150; button(ctx, rx - cw, 80, cw, 34, "Create Laravel", ds, accent=True, ic="plus",
                     press=st.get("create_press", 0), hover=st.get("create_hover", 0))
    st["create_btn"] = (rx - cw, 80, cw, 34)
    button(ctx, rx - cw - 8 - 118, 80, 118, 34, "Park folder", ds, ic="open")
    button(ctx, rx - cw - 16 - 118 - 118, 80, 118, 34, "Proxy sites", ds, ic="sitemap")
    # list panel
    lx, ly, lw, lh = px, 152, 292, Hl - 152 - 22
    fill_rr(ctx, lx, ly, lw, lh, 8, SUBTLE); stroke_rr(ctx, lx, ly, lw, lh, 8, STROKE)
    fill_rr(ctx, lx + 12, ly + 12, lw - 24, 32, 4, WHITE); stroke_rr(ctx, lx + 12, ly + 12, lw - 24, 32, 4, STROKE)
    ctx.rectangle(lx + 13, ly + 42, lw - 26, 1.5); ctx.set_source_rgba(*hx("8A8C91")); ctx.fill()
    text(ctx, "Search sites", lx + 24, ly + 33, "ui", 13, SECOND, ds=ds)
    icon(ctx, "search", lx + lw - 30, ly + 28, 12, SECOND, ds=ds)
    nr = st.get("new_row", 0.0)
    ry = ly + 56
    created = st.get("created", 0.0)
    if nr > 0:
        site_row(ctx, lx, ry - 12 * (1 - nr), lw, "acme.test",
                 "Laravel  ·  PHP 8.4" if created > 0 else "Creating Laravel project…",
                 st.get("new_state", "creating"), ds, alpha=nr, selected=created > 0, creating=st.get("creating", 0))
    shift = 60 * eo3(nr)
    site_row(ctx, lx, ry + shift, lw, "blog.test", "Laravel  ·  PHP 8.3", "running", ds, selected=created <= 0)
    site_row(ctx, lx, ry + shift + 60, lw, "shop.test", "Laravel  ·  PHP 8.4", "running", ds)
    nsites = 3 if nr > 0.5 else 2
    text(ctx, f"{nsites} sites", lx + lw / 2, ly + lh - 16, "ui", 12, SECOND, ds=ds, anchor="c")
    # details
    dx = lx + lw + 20; dw = Wl - 28 - dx
    sel = "acme" if created > 0 else "blog"
    a = 1.0 if created <= 0 else eo3(prog(created, 0, 1))
    ctx.push_group()
    fill_rr(ctx, dx, 160, 44, 44, 8, SITES_T); icon(ctx, "globe", dx + 22, 182, 20, SITES_I, ds=ds)
    text(ctx, sel, dx + 58, 180, "ui_sb", 20, INK, ds=ds)
    text(ctx, f"{sel}.test", dx + 58, 201, "ui", 13, SECOND, ds=ds)
    text(ctx, "Live preview", dx + dw - 112, 188, "ui", 14, INK, ds=ds, anchor="r")
    fill_rr(ctx, dx + dw - 100, 172, 40, 20, 10, BRAND); circle(ctx, dx + dw - 70, 182, 6, WHITE)
    text(ctx, "On", dx + dw - 48, 188, "ui", 14, INK, ds=ds)
    ix = dx + 16
    for ic in ["globe", "open", "term", "pencil", "db", "list", "heart", "share", "more"]:
        icon(ctx, ic, ix, 240, 15, INK, ds=ds); ix += 50
    # dev ops card
    fill_rr(ctx, dx, 266, dw, 64, 8, WHITE); stroke_rr(ctx, dx, 266, dw, 64, 8, STROKE)
    fill_rr(ctx, dx + 16, 280, 250, 36, 4, BRAND)
    icon(ctx, "play", dx + 44, 298, 12, WHITE, ds=ds)
    text(ctx, "Start php artisan dev", dx + 150, 303, "ui", 14, WHITE, ds=ds, anchor="c")
    button(ctx, dx + 276, 280, 90, 36, "Output", ds)
    text(ctx, "php artisan dev: ready", dx + 382, 303, "ui", 13, SECOND, ds=ds)
    # project details card
    text(ctx, "Project details", dx + 2, 364, "ui_sb", 14, INK, ds=ds)
    fill_rr(ctx, dx, 376, dw, 236, 8, WHITE); stroke_rr(ctx, dx, 376, dw, 236, 8, STROKE)
    rows = [("Framework", "Laravel"), ("Runtime", "PHP 8.4  ·  Node.js 24"),
            ("URL", f"https://{sel}.test"), ("Registration", "Created by HerdMe" if sel == "acme" else "Parked"),
            ("Path", f"C:\\Users\\you\\HerdMe\\{sel}")]
    yy = 410
    for i, (k, v) in enumerate(rows):
        text(ctx, k, dx + 18, yy, "ui_sb", 14, INK, ds=ds)
        text(ctx, v, dx + 170, yy, "ui", 14, INK if k != "URL" else BRAND, ds=ds)
        if i < len(rows) - 1:
            ctx.rectangle(dx + 18, yy + 16, dw - 36, 1); ctx.set_source_rgba(*hx("EDEEF0")); ctx.fill()
        yy += 46
    ctx.pop_group_to_source(); ctx.paint_with_alpha(a)
    ctx.restore()

def browser_pill(ctx, x, y, t, typed, secure):
    w, h = 660, 92
    shadow(ctx, x, y, w, h, 46, strength=1.6, spread=22, dy=10)
    fill_rr(ctx, x, y, w, h, 46, WHITE); stroke_rr(ctx, x, y, w, h, 46, STROKE)
    lc = mix(SECOND, OK_GREEN, secure)
    icon(ctx, "lock", x + 54, y + h / 2, 30, lc)
    full = "https://acme.test"
    s = full[:typed]
    pre, dom = s[:8], s[8:]
    wx = x + 92
    wx += text(ctx, pre, wx, y + 60, "ui", 38, SECOND)
    wx += text(ctx, dom, wx, y + 60, "ui_b", 38, INK)
    if typed < len(full) and (t * 3) % 1 < 0.6:
        ctx.rectangle(wx + 3, y + 26, 3, 42); ctx.set_source_rgba(*INK); ctx.fill()
    if secure > 0:
        q = eback(secure, 2.0)
        bw = 150
        ctx.save(); ctx.translate(x + w - 30 - bw / 2, y + h / 2); ctx.scale(q, q)
        fill_rr(ctx, -bw / 2, -22, bw, 44, 22, hx("DFF1E4"))
        icon(ctx, "check", -bw / 2 + 28, 0, 18, OK_GREEN)
        text(ctx, "Secure", -bw / 2 + 48, 9, "ui_b", 24, OK_GREEN)
        ctx.restore()

def caption(ctx, s, t, t0, y=118, color=INK, size=66, x=W / 2, anchor="c"):
    p = eo3(prog(t, t0, t0 + 0.35))
    text(ctx, s, x, y + 26 * (1 - p), "pop_b", size, color, anchor=anchor, alpha=p)

# =====================================================================
# Scene 3: Create Laravel → acme.test over HTTPS
# =====================================================================
def scene3(ctx, t):
    bg(ctx, hx("FFF6F7"))
    confetti(ctx, t + 7, alpha=0.9, avoid=(130, 0, 1790, 1080))
    caption(ctx, "One click. Real HTTPS. .test domains.", t, 0.05)
    S = 1.18
    ox = (W - 1280 * S) / 2
    slide = eo3(prog(t, 0.0, 0.5))
    oy = 196 + 700 * (1 - slide)
    click = 1.25
    st = {}
    st["create_hover"] = prog(t, 1.0, 1.15)
    st["create_press"] = 1.0 - abs(clamp((t - click) / 0.12, -1, 1)) if abs(t - click) < 0.12 else 0.0
    st["new_row"] = eo3(prog(t, 1.35, 1.7))
    st["creating"] = eio(prog(t, 1.45, 2.45))
    st["new_state"] = "running" if t > 2.5 else "creating"
    st["created"] = prog(t, 2.5, 2.85) if t > 2.5 else 0.0
    window(ctx, t, ox, oy, S, st)
    # cursor path: from lower middle to Create Laravel button
    bx, by, bw, bh = st["create_btn"]
    tx, ty = ox + (bx + bw * 0.55) * S, oy + (by + bh * 0.6) * S
    if t < 2.3:
        m = eio(prog(t, 0.45, 1.15))
        cxp = lerp(W * 0.55, tx, m); cyp = lerp(H * 0.8, ty, m)
        cursor(ctx, cxp, cyp, press=st["create_press"], alpha=1 - prog(t, 2.0, 2.3))
    # click ripple
    if click < t < click + 0.45:
        r = eo3(prog(t, click, click + 0.45))
        ctx.arc(tx, ty, 10 + 50 * r, 0, 6.2832); ctx.set_source_rgba(*BRAND[:3], 0.5 * (1 - r)); ctx.set_line_width(4); ctx.stroke()
    # browser pill
    if t > 2.9:
        p = eback(prog(t, 2.9, 3.3), 1.6)
        bx0, by0 = W - 660 - 120, 952
        ctx.save(); ctx.translate(bx0 + 330, by0 + 46); ctx.scale(p, p); ctx.translate(-(bx0 + 330), -(by0 + 46))
        typed = int(round(17 * prog(t, 3.2, 3.85)))
        browser_pill(ctx, bx0, by0, t, typed, eo3(prog(t, 3.95, 4.25)))
        ctx.restore()

# =====================================================================
# Scene 4: the stack
# =====================================================================
SERVICES = [("MariaDB", "db"), ("MySQL", "db"), ("PostgreSQL", "db"), ("MongoDB", "leaf"),
            ("Redis", "bolt"), ("Meilisearch", "search"), ("MinIO", "cube"), ("RustFS", "server")]

def chip(ctx, x, y, w, h, label, tint, ink, ic, p, size=40):
    if p <= 0: return
    q = eback(p, 2.0)
    ctx.save(); ctx.translate(x + w / 2, y + h / 2); ctx.scale(q, q); ctx.translate(-(x + w / 2), -(y + h / 2))
    # flat long shadow
    for i in range(0, 16, 2):
        fill_rr(ctx, x + i, y + i, w, h, 18, (0, 0, 0, 0.012))
    fill_rr(ctx, x, y, w, h, 18, tint)
    if ic:
        icon(ctx, ic, x + 44, y + h / 2, 30, ink)
        text(ctx, label, x + 80, y + h / 2 + size * 0.36, "pop_m", size, ink)
    else:
        text(ctx, label, x + w / 2, y + h / 2 + size * 0.36, "pop_b", size, ink, anchor="c")
    ctx.restore()

def scene4(ctx, t):
    bg(ctx, WHITE)
    caption(ctx, "Your whole stack. Downloaded and verified.", t, 0.0)
    # PHP row
    text(ctx, "PHP", 190, 330, "pop_b", 46, INK, alpha=eo3(prog(t, 0.1, 0.35)))
    for i, v in enumerate(["8.0", "8.1", "8.2", "8.3", "8.4", "8.5"]):
        chip(ctx, 330 + i * 236, 258, 210, 100, v, SITES_T, SITES_I, None, prog(t, 0.2 + i * 0.07, 0.5 + i * 0.07), size=46)
    # services grid 4x2
    text(ctx, "Services", 190, 470, "pop_b", 46, INK, alpha=eo3(prog(t, 0.7, 0.95)))
    for i, (name, ic) in enumerate(SERVICES):
        r, c = divmod(i, 4)
        x = 190 + c * 390; y = 510 + r * 150
        chip(ctx, x, y, 360, 118, name, SERV_T, SERV_I, ic, prog(t, 0.85 + i * 0.085, 1.15 + i * 0.085), size=38)
    p = eo3(prog(t, 1.75, 2.05))
    text(ctx, "Passwords in Windows Credential Manager. Services on loopback only.", W / 2, 900 + 20 * (1 - p),
         "pop_r", 34, SECOND, anchor="c", alpha=p)

# =====================================================================
# Scene 5: Dashboard, English → Arabic RTL
# =====================================================================
DASH = {
    "en": dict(title="Dashboard", sub="Your local development environment at a glance.", ready="Everything is ready",
               detail="Local domains, certificates, and managed services are ready.",
               cards=[("Sites", "3 of 3 running"), ("Services", "2 of 2 running"), ("Mail", "Capture server running"),
                      ("Dumps", "Capture server running")],
               env="Environment", rows=[("Sites environment", "Running"), ("Local domains", "Configured"),
                                         ("HTTPS certificate", "Trusted")]),
    "ar": dict(title="لوحة التحكم", sub="نظرة سريعة على بيئة التطوير المحلية.", ready="كل شيء جاهز",
               detail="النطاقات المحلية والشهادات والخدمات المُدارة جاهزة.",
               cards=[("المواقع", "3 من 3 قيد التشغيل"), ("الخدمات", "2 من 2 قيد التشغيل"),
                      ("البريد", "خادم الالتقاط قيد التشغيل"), ("التفريغات", "خادم الالتقاط قيد التشغيل")],
               env="البيئة", rows=[("بيئة المواقع", "قيد التشغيل"), ("النطاقات المحلية", "مُعدّ"),
                                   ("شهادة HTTPS", "موثوقة")]),
}
CARD_STYLE = [(SITES_T, SITES_I, "folder"), (SERV_T, SERV_I, "apps"), (MAIL_T, MAIL_I, "mail"), (DUMP_T, DUMP_I, "eye")]

def dashboard(ctx, lang, ox, oy, S):
    rtl = lang == "ar"
    d = DASH[lang]
    Wl, Hl = 1320, 690
    fb = "ar_b" if rtl else "ui_sb"; fr = "ar" if rtl else "ui"
    def X(x, w=0): return Wl - x - w if rtl else x
    A = "r" if rtl else "l"; B = "l" if rtl else "r"
    ctx.save(); ctx.translate(ox, oy); ctx.scale(S, S); ds = S
    shadow(ctx, 0, 0, Wl, Hl, 10, strength=1.2)
    fill_rr(ctx, 0, 0, Wl, Hl, 10, WHITE); stroke_rr(ctx, 0, 0, Wl, Hl, 10, STROKE)
    text(ctx, d["title"], X(36), 66, fb, 30, INK, anchor=A, ds=ds, rtl=rtl)
    text(ctx, d["sub"], X(36), 98, fr, 15, SECOND, anchor=A, ds=ds, rtl=rtl)
    # health card
    fill_rr(ctx, 36, 124, Wl - 72, 104, 8, SUBTLE); stroke_rr(ctx, 36, 124, Wl - 72, 104, 8, STROKE)
    fill_rr(ctx, X(60, 56), 148, 56, 56, 10, SERV_T); icon(ctx, "check", X(60, 56) + 28, 176, 26, SERV_I, ds=ds)
    text(ctx, d["ready"], X(134), 171, fb, 22, INK, anchor=A, ds=ds, rtl=rtl)
    text(ctx, d["detail"], X(134), 199, fr, 15, SECOND, anchor=A, ds=ds, rtl=rtl)
    # summary cards
    cw = (Wl - 72 - 3 * 16) / 4
    for i, ((lab, val), (tint, ink, ic)) in enumerate(zip(d["cards"], CARD_STYLE)):
        cx = 36 + i * (cw + 16)
        cx = Wl - cx - cw if rtl else cx
        fill_rr(ctx, cx, 250, cw, 128, 8, WHITE); stroke_rr(ctx, cx, 250, cw, 128, 8, STROKE)
        ix = cx + cw - 20 - 40 if rtl else cx + 20
        fill_rr(ctx, ix, 270, 40, 40, 8, tint); icon(ctx, ic, ix + 20, 290, 17, ink, ds=ds)
        circle(ctx, cx + 24 if rtl else cx + cw - 24, 290, 5, OK_GREEN)
        tx = cx + cw - 20 if rtl else cx + 20
        text(ctx, lab, tx, 340, fb, 17, INK, anchor=A, ds=ds, rtl=rtl)
        text(ctx, val, tx, 362, fr, 13.5, SECOND, anchor=A, ds=ds, rtl=rtl)
    # environment rows
    text(ctx, d["env"], X(38), 420, fb, 16, INK, anchor=A, ds=ds, rtl=rtl)
    icons = ["server", "globe", "lock"]
    for i, (k, v) in enumerate(d["rows"]):
        y = 436 + i * 76
        fill_rr(ctx, 36, y, Wl - 72, 68, 8, SUBTLE); stroke_rr(ctx, 36, y, Wl - 72, 68, 8, STROKE)
        icon(ctx, icons[i], X(66), y + 34, 18, INK, ds=ds)
        text(ctx, k, X(100), y + 40, fr, 16, INK, anchor=A, ds=ds, rtl=rtl)
        bw = text_w(v, fr, 14, ds, rtl) + 40
        bx = X(Wl - 60 - bw, bw) if not rtl else 60
        bx = 60 if rtl else Wl - 60 - bw
        fill_rr(ctx, bx, y + 20, bw, 28, 14, hx("DFF1E4"))
        circle(ctx, bx + bw - 16 if rtl else bx + 16, y + 34, 4.5, OK_GREEN)
        text(ctx, v, bx + bw - 28 if rtl else bx + 28, y + 39, fr, 14, hx("0B5A0B"), anchor=A, ds=ds, rtl=rtl)
    ctx.restore()

def scene5(ctx, t):
    bg(ctx, hx("F2F7FF"))
    confetti(ctx, t + 11, alpha=0.9, avoid=(130, 0, 1790, 1080), cols=[MAIL_T, SERV_T, SITES_T, DUMP_T])
    flip = 1.25
    p1 = eo3(prog(t, 0.0, 0.35))
    text(ctx, "English.", W / 2 - 20, 118 + 26 * (1 - p1), "pop_b", 66, INK, anchor="r", alpha=p1)
    p2 = eo3(prog(t, flip + 0.1, flip + 0.45))
    text(ctx, "العربية.", W / 2 + 20, 122 + 26 * (1 - p2), "ar_b", 64, BRAND, anchor="l", alpha=p2, rtl=True)
    S = 1.2
    ox, oy = (W - 1320 * S) / 2, 190
    if t < flip:
        sx = 1.0 if t < flip - 0.2 else math.cos(prog(t, flip - 0.2, flip) * math.pi / 2); lang = "en"
    else:
        sx = math.sin(prog(t, flip, flip + 0.22) * math.pi / 2); lang = "ar"
    rise = eo3(prog(t, 0.0, 0.45))
    oy += 500 * (1 - rise)
    ctx.save(); ctx.translate(W / 2, 0); ctx.scale(max(sx, 0.001), 1); ctx.translate(-W / 2, 0)
    dashboard(ctx, lang, ox, oy, S)
    ctx.restore()

# =====================================================================
# Scene 6: business model → outro
# =====================================================================
def scene6(ctx, t):
    bg(ctx, WHITE)
    out = 4 * BEAT  # outro at beat 37
    slam = 2 * BEAT  # beat 35
    if t < out:
        leave = eio(prog(t, out - 0.22, out))
        p = eo3(prog(t, 0.05, 0.4))
        text(ctx, "Our business model:", W / 2, 440 + 30 * (1 - p) - 120 * leave, "pop_m", 96, INK, anchor="c",
             alpha=p * (1 - leave))
        if t >= slam:
            q = prog(t, slam, slam + 0.25)
            sc = lerp(1.5, 1.0, eback(q, 1.3))
            text(ctx, "There isn't one.", W / 2, 660 - 120 * leave, "pop_b", 170, BRAND, anchor="c", scale=sc,
                 alpha=clamp(q * 4) * (1 - leave))
        return
    u = t - out
    confetti(ctx, t, alpha=eo3(prog(u, 0.2, 0.7)), avoid=(300, 150, 1620, 960))
    p = prog(u, 0.0, 0.4)
    if p > 0: draw_icon(ctx, W / 2 - 300, 400, 200 * eback(p, 2.0), long_shadow=eo3(prog(u, 0.3, 0.7)), sh_len=0.45, sh_max=0.14)
    q = eo3(prog(u, 0.12, 0.45))
    text(ctx, "HerdMe", W / 2 - 160 + 30 * (1 - q), 450, "pop_b", 150, INK, alpha=q)
    r = eo3(prog(u, 0.3, 0.6))
    text(ctx, "No subscriptions  ·  No license keys  ·  MIT", W / 2, 650 + 20 * (1 - r), "pop_m", 54, INK,
         anchor="c", alpha=r)
    s = eo3(prog(u, 0.45, 0.75))
    wbar = text_w("github.com/Hamad3bdulla/herdme", "pop_m", 44) + 80
    fill_rr(ctx, W / 2 - wbar / 2, 740 + 20 * (1 - s), wbar, 84, 42, BRAND, alpha=s)
    text(ctx, "github.com/Hamad3bdulla/herdme", W / 2, 797 + 20 * (1 - s), "pop_m", 44, WHITE, anchor="c", alpha=s)

SCENES = [(S1, scene1), (S2, scene2), (S3, scene3), (S4, scene4), (S5, scene5), (S6, scene6)]

def frame(t):
    surf = cairo.ImageSurface(cairo.FORMAT_ARGB32, W, H)
    ctx = cairo.Context(surf)
    ctx.set_antialias(cairo.ANTIALIAS_BEST)
    for i, (t0, fn) in enumerate(SCENES):
        t1 = SCENES[i + 1][0] if i + 1 < len(SCENES) else DUR + 1
        if t0 <= t < t1:
            fn(ctx, t - t0); break
    return surf

def to_rgb(surf):
    buf = np.ndarray((H, surf.get_stride() // 4, 4), np.uint8, surf.get_data())
    return buf[:, :W, [2, 1, 0]]

if __name__ == "__main__":
    mode = sys.argv[1]
    if mode == "stills":
        outdir = sys.argv[2]; os.makedirs(outdir, exist_ok=True)
        for ts in sys.argv[3:]:
            tt = float(ts)
            Image.fromarray(to_rgb(frame(tt)).copy()).save(os.path.join(outdir, f"still_{tt:06.2f}.png"))
    elif mode == "video":
        # raw rgb frames to stdout; args: start end
        a, b = int(sys.argv[2]), int(sys.argv[3])
        out = sys.stdout.buffer
        for n in range(a, b):
            out.write(np.ascontiguousarray(to_rgb(frame(n / FPS))).tobytes())
