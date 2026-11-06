
// JPEG 1x1 minimal valid enough for dimension parsing (SOI + SOF0 + EOI).
function fakeJpeg(width, height, components = 3) {
  const sof = Buffer.alloc(8 + components * 3);
  sof[0] = 8;
  sof.writeUInt16BE(height, 1);
  sof.writeUInt16BE(width, 3);
  sof[5] = components;
  for (let i = 0; i < components; i++) sof.set([i + 1, 0x11, 0], 6 + i * 3);
  const len = Buffer.alloc(2);
  len.writeUInt16BE(sof.length + 2);
  return Buffer.concat([Buffer.from([0xff, 0xd8, 0xff, 0xc0]), len, sof, Buffer.from([0xff, 0xd9])]);
}


module.exports = { fakeJpeg };
