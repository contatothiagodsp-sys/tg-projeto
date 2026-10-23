# Gera os arquivos visuais do tHg a partir da arte oficial (robô + "THG").
# Fontes: robot.png e thg-wordmark*.png (recortados da arte, sem o fundo). Saída: Assets/, installer/, docs/.
from PIL import Image, ImageDraw, ImageFont
import os
R = '/home/claude/ResetExplode'
robot = Image.open('mascot.png'); wm = Image.open('thg-wordmark.png'); wmd = Image.open('thg-wordmark-dark.png')
TILE = (10, 15, 28, 255)  # azul-marinho da arte

def fit(im, box):
    im = im.copy(); im.thumbnail(box, Image.LANCZOS); return im

def square(size, pad=0.06, tile=False, radius=0.22):
    c = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    if tile: ImageDraw.Draw(c).rounded_rectangle([0, 0, size - 1, size - 1], int(size * radius), fill=TILE)
    p = int(size * pad); r = fit(robot, (size - 2 * p, size - 2 * p))
    c.alpha_composite(r, ((size - r.width) // 2, (size - r.height) // 2)); return c

A = f'{R}/Assets'
square(256).save(f'{A}/logo.png'); square(256).save(f'{A}/logo-dark.png')
square(128, pad=0.12, tile=True).save(f'{A}/logo-badge.png')
ico = square(256, pad=0.10, tile=True)
ico.save(f'{A}/app.ico', sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
for name, src in (('thg-wordmark.png', wm), ('thg-wordmark-dark.png', wmd)):
    fit(src, (1000, 96)).save(f'{A}/{name}')

F = '/usr/share/fonts/opentype/inter/'
def font(n, s): return ImageFont.truetype(F + n, s)

# Instalador: painel lateral (164x314) e ícone pequeno (55x55), em 1x e 2x
for k in (1, 2):
    w, h = 164 * k, 314 * k
    im = Image.new('RGBA', (w, h), TILE); d = ImageDraw.Draw(im)
    r = fit(robot, (120 * k, 130 * k)); im.alpha_composite(r, ((w - r.width) // 2, 46 * k))
    m = fit(wm, (120 * k, 40 * k)); im.alpha_composite(m, ((w - m.width) // 2, 196 * k))
    t = 'OPTIMIZATION'; f = font('Inter-Light.otf', 12 * k)
    d.text(((w - d.textlength(t, font=f)) / 2, 236 * k), t, font=f, fill=(235, 240, 250))
    t = 'desempenho · segurança'; f = font('Inter-Medium.otf', 9 * k)
    d.text(((w - d.textlength(t, font=f)) / 2, 284 * k), t, font=f, fill=(110, 130, 160))
    im.convert('RGB').save(f'{R}/installer/wizard-{k}x.bmp')
    s = 55 * k; sm = Image.new('RGBA', (s, s), (255, 255, 255, 255)); sm.alpha_composite(square(s, pad=0.04, tile=True))
    sm.convert('RGB').save(f'{R}/installer/wizard-small-{k}x.bmp')

# Banner do README
bn = Image.new('RGBA', (1280, 360), TILE); d = ImageDraw.Draw(bn)
r = fit(robot, (260, 280)); bn.alpha_composite(r, (110, (360 - r.height) // 2))
m = fit(wm, (520, 130)); bn.alpha_composite(m, (420, 78))
d.text((426, 222), 'OPTIMIZATION', font=font('Inter-Light.otf', 50), fill=(240, 244, 250))
d.text((430, 296), 'DESEMPENHO  ·  SEGURANÇA  ·  DIAGNÓSTICO', font=font('Inter-Medium.otf', 22), fill=(110, 135, 170))
bn.convert('RGB').save(f'{R}/docs/banner.png')
print('ok')
