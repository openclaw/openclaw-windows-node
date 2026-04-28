const fs = require('fs');
const zlib = require('zlib');

const width = 1440;
const height = 1504;
const rawFile = process.argv[1];
const outFile = process.argv[2];

const rgba = fs.readFileSync(rawFile);

// Build raw scanlines: filter byte (0) + RGBA row data
const rowSize = width * 4;
const filtered = Buffer.alloc(height * (1 + rowSize));
for (let y = 0; y < height; y++) {
    filtered[y * (1 + rowSize)] = 0; // filter: None
    rgba.copy(filtered, y * (1 + rowSize) + 1, y * rowSize, (y + 1) * rowSize);
}

const compressed = zlib.deflateSync(filtered);

function crc32(buf) {
    let crc = 0xFFFFFFFF;
    for (let i = 0; i < buf.length; i++) {
        crc ^= buf[i];
        for (let j = 0; j < 8; j++) {
            crc = (crc >>> 1) ^ (crc & 1 ? 0xEDB88320 : 0);
        }
    }
    return (crc ^ 0xFFFFFFFF) >>> 0;
}

function makeChunk(type, data) {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length);
    const typeAndData = Buffer.concat([Buffer.from(type), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(typeAndData));
    return Buffer.concat([len, typeAndData, crc]);
}

// IHDR
const ihdr = Buffer.alloc(13);
ihdr.writeUInt32BE(width, 0);
ihdr.writeUInt32BE(height, 4);
ihdr[8] = 8;  // bit depth
ihdr[9] = 6;  // color type: RGBA
ihdr[10] = 0; // compression
ihdr[11] = 0; // filter
ihdr[12] = 0; // interlace

const png = Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), // PNG signature
    makeChunk('IHDR', ihdr),
    makeChunk('IDAT', compressed),
    makeChunk('IEND', Buffer.alloc(0))
]);

fs.writeFileSync(outFile, png);
fs.unlinkSync(rawFile); // cleanup raw
console.log('PNG written: ' + outFile + ' (' + png.length + ' bytes)');