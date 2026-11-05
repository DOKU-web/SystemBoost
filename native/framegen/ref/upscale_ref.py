"""
SystemBoost FrameGen v0.6 - upscaler reference (numpy). Mirrors shaders/upscale.hlsl.

Pass 1  Catmull-Rom bicubic resample, clamped to the min/max of the 2x2 source
        texels around the sample (no dark/bright halos around edges).
Pass 2  Contrast-adaptive sharpening on the output: unsharp mask whose strength
        falls off where local contrast is already high (avoids over-sharpening
        text and edges), clamped to the 3x3 neighbourhood range.
"""
import numpy as np


def _cubic_w(x):
    """Catmull-Rom weights for fractional offset x (array)."""
    x2, x3 = x * x, x * x * x
    w0 = -0.5 * x3 + x2 - 0.5 * x
    w1 = 1.5 * x3 - 2.5 * x2 + 1.0
    w2 = -1.5 * x3 + 2.0 * x2 + 0.5 * x
    w3 = 0.5 * x3 - 0.5 * x2
    return w0, w1, w2, w3


def resample_bicubic_clamped(src, out_h, out_w, anti_ring=True):
    h, w = src.shape[:2]
    ys = (np.arange(out_h) + 0.5) * h / out_h - 0.5
    xs = (np.arange(out_w) + 0.5) * w / out_w - 0.5
    y0 = np.floor(ys).astype(int)
    x0 = np.floor(xs).astype(int)
    fy = (ys - y0)[:, None, None]
    fx = (xs - x0)[None, :, None]
    wy = _cubic_w(fy)
    wx = _cubic_w(fx)
    out = np.zeros((out_h, out_w, src.shape[2]), np.float32)
    lo = np.full_like(out, np.inf)
    hi = np.full_like(out, -np.inf)
    for j in range(4):
        yy = np.clip(y0 + j - 1, 0, h - 1)
        row = src[yy]                                   # (out_h, w, c)
        for i in range(4):
            xx = np.clip(x0 + i - 1, 0, w - 1)
            v = row[:, xx]                              # (out_h, out_w, c)
            out += wy[j] * wx[i] * v
            if j in (1, 2) and i in (1, 2):
                lo = np.minimum(lo, v)
                hi = np.maximum(hi, v)
    if anti_ring:
        out = np.clip(out, lo, hi)
    return np.clip(out, 0, 1)


def adaptive_sharpen(img, amount=0.5):
    """amount 0..1. Strength per pixel = amount * (1 - local_range)^2."""
    if amount <= 0:
        return img
    p = np.pad(img, ((1, 1), (1, 1), (0, 0)), mode='edge')
    h, w = img.shape[:2]
    nb = np.stack([p[dy:dy + h, dx:dx + w] for dy in range(3) for dx in range(3)], 0)
    lo, hi = nb.min(0), nb.max(0)
    cross = (p[0:h, 1:w + 1] + p[2:h + 2, 1:w + 1] + p[1:h + 1, 0:w] + p[1:h + 1, 2:w + 2]) / 4
    rng = (hi - lo).max(-1, keepdims=True)
    k = amount * 1.5 * (1 - np.clip(rng, 0, 1)) ** 2
    out = img + k * (img - cross)
    return np.clip(out, lo, hi)


def upscale(src, out_h, out_w, sharpness=0.5):
    return adaptive_sharpen(resample_bicubic_clamped(src, out_h, out_w), sharpness)


def bilinear_resize(src, out_h, out_w):
    h, w = src.shape[:2]
    ys = np.clip((np.arange(out_h) + 0.5) * h / out_h - 0.5, 0, h - 1)
    xs = np.clip((np.arange(out_w) + 0.5) * w / out_w - 0.5, 0, w - 1)
    y0 = np.floor(ys).astype(int); x0 = np.floor(xs).astype(int)
    y1 = np.minimum(y0 + 1, h - 1); x1 = np.minimum(x0 + 1, w - 1)
    fy = (ys - y0)[:, None, None]; fx = (xs - x0)[None, :, None]
    a = src[y0][:, x0] * (1 - fx) + src[y0][:, x1] * fx
    b = src[y1][:, x0] * (1 - fx) + src[y1][:, x1] * fx
    return a * (1 - fy) + b * fy
