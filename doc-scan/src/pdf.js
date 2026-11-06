function jpegInfo(buf) {
  if (buf.length < 4 || buf[0] !== 0xff || buf[1] !== 0xd8) throw new Error('Data bukan JPEG');
  let i = 2;
  while (i + 9 < buf.length) {
    if (buf[i] !== 0xff) {
      i++;
      continue;
    }
    const marker = buf[i + 1];
    if (marker === 0xff) {
      i++;
      continue;
    }
    if (marker >= 0xc0 && marker <= 0xcf && ![0xc4, 0xc8, 0xcc].includes(marker)) {
      return { height: buf.readUInt16BE(i + 5), width: buf.readUInt16BE(i + 7), components: buf[i + 9] };
    }
    if (marker === 0xd8 || marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) {
      i += 2;
      continue;
    }
    i += 2 + buf.readUInt16BE(i + 2);
  }
  throw new Error('Ukuran gambar JPEG tidak terbaca');
}

function jpegsToPdf(jpegs, dpi) {
  if (jpegs.length === 0) throw new Error('Tidak ada halaman untuk dijadikan PDF');
  const chunks = [];
  const offsets = [];
  let pos = 0;
  const push = (b) => {
    const buf = Buffer.isBuffer(b) ? b : Buffer.from(b, 'latin1');
    chunks.push(buf);
    pos += buf.length;
  };
  const object = (id, dict, stream) => {
    offsets[id] = pos;
    push(`${id} 0 obj\n`);
    if (stream) {
      push(`<< ${dict} /Length ${stream.length} >>\nstream\n`);
      push(stream);
      push('\nendstream\nendobj\n');
    } else {
      push(`${dict}\nendobj\n`);
    }
  };

  push('%PDF-1.4\n');
  const pageIds = jpegs.map((_, k) => 3 + k * 3);
  object(1, '<< /Type /Catalog /Pages 2 0 R >>');
  object(2, `<< /Type /Pages /Count ${jpegs.length} /Kids [${pageIds.map((id) => `${id} 0 R`).join(' ')}] >>`);

  jpegs.forEach((jpeg, k) => {
    const { width, height, components } = jpegInfo(jpeg);
    const space = { 1: '/DeviceGray', 3: '/DeviceRGB' }[components];
    if (!space) throw new Error('Mode warna JPEG tidak didukung');
    const w = ((width * 72) / dpi).toFixed(2);
    const h = ((height * 72) / dpi).toFixed(2);
    const pageId = 3 + k * 3;
    object(
      pageId,
      `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${w} ${h}] /Resources << /XObject << /Im0 ${pageId + 2} 0 R >> >> /Contents ${pageId + 1} 0 R >>`,
    );
    object(pageId + 1, '', Buffer.from(`q ${w} 0 0 ${h} 0 0 cm /Im0 Do Q`, 'latin1'));
    object(
      pageId + 2,
      `/Type /XObject /Subtype /Image /Width ${width} /Height ${height} /ColorSpace ${space} /BitsPerComponent 8 /Filter /DCTDecode`,
      jpeg,
    );
  });

  const total = 3 + jpegs.length * 3;
  const xrefPos = pos;
  let xref = `xref\n0 ${total}\n0000000000 65535 f \n`;
  for (let id = 1; id < total; id++) xref += `${String(offsets[id]).padStart(10, '0')} 00000 n \n`;
  push(xref);
  push(`trailer\n<< /Size ${total} /Root 1 0 R >>\nstartxref\n${xrefPos}\n%%EOF\n`);
  return Buffer.concat(chunks);
}

module.exports = { jpegInfo, jpegsToPdf };
