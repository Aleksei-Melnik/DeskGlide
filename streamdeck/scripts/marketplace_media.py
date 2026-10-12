"""Build Marketplace artwork from DeskGlide assets and a real CI-rendered settings page.

Requires Pillow. Run with --settings PATH/TO/Host-10.png.
No private profile data or third-party device artwork is used.
"""
import argparse
import math
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "marketplace" / "media"
W, H = 1920, 960
PINK, CYAN, WHITE, MUTED = "#ff398e", "#38d7e3", "#f5f3fa", "#b1aabc"
FONT = Path("C:/Windows/Fonts")


def font(size, bold=False):
    return ImageFont.truetype(str(FONT / ("segoeuib.ttf" if bold else "segoeui.ttf")), size)


def text(draw, xy, value, size=30, color=WHITE, bold=False, anchor=None):
    draw.text(xy, value, font=font(size, bold), fill=color, anchor=anchor)


def background(kicker, title, subtitle):
    image = Image.new("RGB", (W, H), "#0b0c13")
    glow = Image.new("RGB", (W, H))
    d = ImageDraw.Draw(glow)
    d.ellipse((600, -450, 1800, 800), fill="#372039")
    d.ellipse((1300, 300, 2100, 1400), fill="#11232c")
    glow = glow.filter(ImageFilter.GaussianBlur(130))
    image = Image.blend(image, glow, .48)
    d = ImageDraw.Draw(image)
    text(d, (100, 68), kicker.upper(), 24, PINK, True)
    text(d, (100, 116), title, 64, bold=True)
    text(d, (100, 213), subtitle, 29, MUTED)
    d.line((100, 874, 1820, 874), fill="#33303e", width=1)
    text(d, (100, 897), "DeskGlide  /  Stream Deck plugin", 23, MUTED)
    text(d, (1820, 897), "By Yami", 23, MUTED, anchor="ra")
    return image


def card(d, rect, radius=28):
    d.rounded_rectangle(rect, radius, fill="#191923", outline="#39303f", width=2)


def glyph(d, kind, x, y, scale, color):
    # Matches the five in-plugin line icons, scaled for illustration.
    def points(ps):
        return [(x+a*scale, y+b*scale) for a, b in ps]
    def line(ps):
        d.line(points(ps), fill=color, width=round(3.5*scale), joint="curve")
    def box(rect):
        d.rectangle((x+rect[0]*scale, y+rect[1]*scale, x+rect[2]*scale, y+rect[3]*scale), outline=color, width=round(3.5*scale))
    if kind in ("save", "replay"):
        circle = [(44+23*math.cos(a), 43+23*math.sin(a)) for a in [math.radians(250+i*300/80) for i in range(81)]]
        line(circle); line([(20, 18), (20, 36), (38, 36)])
        if kind == "save": box((39, 45, 56, 62))
        else: box((40, 39, 47, 55)); box((51, 39, 58, 55))
    elif kind == "lock":
        box((29, 39, 59, 68))
        line([(35, 39), (35, 28), (37, 23), (44, 21), (51, 23), (53, 28), (53, 39)])
        line([(44, 49), (44, 57)])
    else:
        box((19, 24, 69, 56)); line([(44, 56), (44, 65)]); line([(33, 65), (55, 65)])
        if kind == "stream": line([(36, 33), (55, 41), (36, 49), (36, 33)])
        else: line([(53, 34), (42, 47), (37, 39)])


def key(d, rect, kind, label, color=PINK, fill=0):
    x, y, w = rect
    card(d, (x, y, x+w, y+w), 24)
    scale=w/115
    glyph(d, kind, x+(w-88*scale)/2, y+5, scale, color)
    text(d, (x+w/2, y+w-51), label, round(w*.145), bold=True, anchor="mm")
    d.rounded_rectangle((x+18, y+w-20, x+w-18, y+w-14), 3, fill="#37313f")
    if fill:
        d.rounded_rectangle((x+18, y+w-20, x+18+(w-36)*fill, y+w-14), 3, fill=color)


def save(image, name):
    assert image.size == (W, H)
    image.save(OUT / name, optimize=True)


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--settings", type=Path, required=True)
    args=parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    logo=Image.open(ROOT / "com.yami.deskglide.sdPlugin/imgs/plugin@2x.png").convert("RGBA")
    logo.resize((288, 288), Image.Resampling.LANCZOS).save(OUT / "icon.png", optimize=True)

    im=background("DeskGlide", "Your PCs. One touch.", "Instant replay · KVM · Mouse transitions · Screen streaming")
    d=ImageDraw.Draw(im)
    im.paste(logo.resize((260, 260), Image.Resampling.LANCZOS), (110, 390), logo.resize((260, 260), Image.Resampling.LANCZOS))
    for i, (kind, label, color) in enumerate([("save", "2:00", PINK), ("kvm", "KVM · 2", CYAN), ("lock", "Locked", PINK), ("replay", "Replay", PINK), ("stream", "Streaming", CYAN)]):
        key(d, (472+i*258, 410, 226), kind, label, color, .4 if i == 0 else 0)
    text(d, (472, 688), "Live feedback. Direct commands. No hotkey setup.", 31, MUTED)
    save(im, "thumbnail.png")

    im=background("Instant replay", "Save the moment. See the result.", "Watch the buffer fill, then track export and clip delivery from the key.")
    d=ImageDraw.Draw(im)
    states=[("2:00", PINK, .4, "Buffer ready"), ("Saving", "#bd70ff", .4, "Exporting clip"), ("Queued", "#e3b35d", .4, "Copying to folder"), ("Saved", "#38dfab", 0, "Delivery complete")]
    for i,(label,color,fill,caption) in enumerate(states):
        x=125+i*450
        key(d,(x,358,285),"save",label,color,fill)
        text(d,(x+142,695),caption,31,anchor="mm")
    text(d,(100,794),"Status examples · Saved appears after the file reaches its destination.",24,MUTED)
    save(im,"gallery-1.png")

    im=background("Setup", "Install. Add a key. Choose your PC.", "The Stream Deck tab in DeskGlide keeps installation, connection status and help together.")
    shot=Image.open(args.settings).convert("RGB")
    shot.thumbnail((1310,555),Image.Resampling.LANCZOS)
    im.paste(shot, (W-shot.width-100, 290))
    d=ImageDraw.Draw(im)
    for i,(heading,detail) in enumerate([("1  Install plugin", "Built into DeskGlide"), ("2  Add an action", "Drag it onto a key"), ("3  Choose the target", "This PC or paired controller")]):
        text(d,(100,338+i*150),heading,35,bold=True)
        text(d,(100,391+i*150),detail,26,MUTED)
    text(d,(W-100,824),"Actual DeskGlide settings · Windows 10/11",22,MUTED,anchor="ra")
    save(im,"gallery-2.png")

    im=background("Connected PCs", "Control the PC that does the work.", "Use local actions, or send replay and streaming commands to your paired controller PC.")
    d=ImageDraw.Draw(im)
    for x, heading, lines, color in [(100,"This PC",["Open KVM","Choose a remote PC or server","Lock mouse transitions"],CYAN),(1035,"Paired controller PC",["Save replay on your gaming PC","Toggle replay or screen streaming","View its live buffer and status"],PINK)]:
        card(d,(x,340,x+785,750))
        text(d,(x+44,382),heading,41,color,True)
        for i,line in enumerate(lines): text(d,(x+44,468+i*70),line,29)
    d.line((920,532,998,532),fill=CYAN,width=5)
    d.polygon([(998,532),(982,522),(982,542)],fill=CYAN)
    text(d,(100,797),"Pair the PCs in DeskGlide first. Open KVM runs on the controller PC.",25,MUTED)
    save(im,"gallery-3.png")


if __name__ == "__main__":
    main()
