# Recorta a arte nova e tira só o fundo (a área escura ligada às bordas). O visor escuro do robô fica.
from PIL import Image, ImageDraw, ImageFilter
import numpy as np

def unbg(im, bg):
    a = np.asarray(im.convert('RGBA')).astype(np.float32)
    rgb = a[..., :3]
    bgv = np.array(bg, np.float32)
    d = np.sqrt(((rgb - bgv) ** 2).sum(-1))
    dist_alpha = np.clip((d - 6) / 40, 0, 1)
    # fundo = pixels parecidos com o fundo E ligados às bordas (flood fill)
    like = Image.fromarray(np.where(d < 28, 255, 0).astype(np.uint8), 'L').copy()
    h, w = d.shape
    for pt in [(0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)]:
        if like.getpixel(pt) == 255: ImageDraw.floodfill(like, pt, 128)
    region = np.array(like) == 128
    near = np.asarray(Image.fromarray((region * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(7))) > 0
    alpha = np.where(near, dist_alpha, 1.0)
    safe = np.maximum(alpha, 1e-3)[..., None]
    rgb2 = np.where(near[..., None], np.clip((rgb - bgv * (1 - alpha[..., None])) / safe, 0, 255), rgb)
    return Image.fromarray(np.dstack([rgb2, alpha * 255]).astype(np.uint8), 'RGBA')

def bbox(im): return im.getchannel('A').point(lambda v: 255 if v > 20 else 0).getbbox()

def dark_letters(im):
    """Versão para tema claro: partes brancas/cinza (sem cor) viram quase pretas; o azul fica."""
    a = np.asarray(im).astype(np.float32)
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    sat = a[..., :3].max(-1) - a[..., :3].min(-1)
    white = sat < 40
    for c in range(3): a[..., c] = np.where(white, 17, a[..., c])
    return Image.fromarray(a.astype(np.uint8), 'RGBA')

w = unbg(Image.open('wordmark-src.png'), (5, 9, 18)).crop((180, 440, 1080, 690)); w = w.crop(bbox(w))
w.save('thg-wordmark.png'); dark_letters(w).save('thg-wordmark-dark.png')
m = unbg(Image.open('mascot-src.png'), (7, 9, 22)).crop((330, 180, 920, 900)); m = m.crop(bbox(m))
m.save('mascot.png')
print(w.size, m.size)
