#!/usr/bin/env node
// Count opaque pixels per horizontal band of a lab render (RGBA8 PNG from Unity EncodeToPNG).
// Usage: node tools/alpha-band.mjs <png> [y0 y1] ...   → opaque count + bbox, per band
// Zero deps: inflate IDAT with zlib, undo the 5 PNG row filters.
import fs from 'node:fs';
import zlib from 'node:zlib';
import { pathToFileURL } from 'node:url';

export function decodePng(file) {
  const buf = fs.readFileSync(file);
  let pos = 8, w = 0, h = 0, bitDepth = 0, colorType = 0;
  const idat = [];
  while (pos < buf.length) {
    const len = buf.readUInt32BE(pos);
    const type = buf.toString('ascii', pos + 4, pos + 8);
    const data = buf.subarray(pos + 8, pos + 8 + len);
    if (type === 'IHDR') { w = data.readUInt32BE(0); h = data.readUInt32BE(4); bitDepth = data[8]; colorType = data[9]; }
    if (type === 'IDAT') idat.push(data);
    pos += 12 + len;
  }
  if (bitDepth !== 8 || (colorType !== 6 && colorType !== 2)) throw new Error(`unsupported PNG: depth ${bitDepth} type ${colorType}`);
  const bpp = colorType === 6 ? 4 : 3;
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const stride = w * bpp;
  const out = Buffer.alloc(h * stride);
  for (let y = 0; y < h; y++) {
    const f = raw[y * (stride + 1)];
    const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
    for (let x = 0; x < stride; x++) {
      const a = x >= bpp ? out[y * stride + x - bpp] : 0;
      const b = y > 0 ? out[(y - 1) * stride + x] : 0;
      const c = x >= bpp && y > 0 ? out[(y - 1) * stride + x - bpp] : 0;
      let v = line[x];
      if (f === 1) v += a;
      else if (f === 2) v += b;
      else if (f === 3) v += (a + b) >> 1;
      else if (f === 4) { const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
      out[y * stride + x] = v & 255;
    }
  }
  return { w, h, bpp, px: out };
}

// pathToFileURL, not string-building: "Claude Space" encodes as %20 in import.meta.url.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [file, ...rest] = process.argv.slice(2);
  const img = decodePng(file);
  const bands = [];
  for (let i = 0; i + 1 < rest.length; i += 2) bands.push([Number(rest[i]), Number(rest[i + 1])]);
  if (!bands.length) bands.push([0, img.h]);
  // PNG rows run top→bottom; Unity's ReadPixels is bottom-up, so the saved file is upside down
  // relative to the screen ONLY if nothing flipped it. Report both conventions' top band.
  for (const [y0, y1] of bands) {
    let n = 0, minX = 1e9, maxX = -1, minY = 1e9, maxY = -1;
    for (let y = y0; y < Math.min(y1, img.h); y++) for (let x = 0; x < img.w; x++) {
      const alpha = img.bpp === 4 ? img.px[(y * img.w + x) * 4 + 3] : 255;
      if (alpha > 32) { n++; if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
    }
    console.log(`${file.split(/[\\/]/).pop()}  rows ${y0}-${y1}: opaque ${n}${n ? `  bbox x${minX}-${maxX} y${minY}-${maxY}` : ''}`);
  }
}
