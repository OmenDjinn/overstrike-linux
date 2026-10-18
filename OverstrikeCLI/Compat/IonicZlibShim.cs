// overstrike-cli -- Linux command-line port of Overstrike.
// Licensed under GPLv3, same as Overstrike. See LICENSE.txt.
//
// Tiny replacement for the parts of Ionic.Zlib that DAT1/TOC.cs uses,
// implemented on top of .NET's built-in System.IO.Compression.ZLibStream.

using System;
using System.IO;
using SysZ = System.IO.Compression;

namespace Ionic.Zlib {
	public enum CompressionMode { Compress, Decompress }
	public enum CompressionLevel { None, BestSpeed, Default, BestCompression }

	public class ZlibStream : Stream {
		private readonly SysZ.ZLibStream _inner;

		public ZlibStream(Stream stream, CompressionMode mode, CompressionLevel level = CompressionLevel.Default) {
			_inner = mode == CompressionMode.Compress
				? new SysZ.ZLibStream(stream, Map(level), leaveOpen: true)
				: new SysZ.ZLibStream(stream, SysZ.CompressionMode.Decompress, leaveOpen: true);
		}

		private static SysZ.CompressionLevel Map(CompressionLevel level) => level switch {
			CompressionLevel.None => SysZ.CompressionLevel.NoCompression,
			CompressionLevel.BestSpeed => SysZ.CompressionLevel.Fastest,
			CompressionLevel.BestCompression => SysZ.CompressionLevel.SmallestSize,
			_ => SysZ.CompressionLevel.Optimal,
		};

		public static byte[] UncompressBuffer(byte[] compressed) {
			using var input = new MemoryStream(compressed);
			using var z = new SysZ.ZLibStream(input, SysZ.CompressionMode.Decompress);
			using var output = new MemoryStream();
			z.CopyTo(output);
			return output.ToArray();
		}

		public static byte[] CompressBuffer(byte[] data) {
			using var output = new MemoryStream();
			using (var z = new SysZ.ZLibStream(output, SysZ.CompressionLevel.SmallestSize, leaveOpen: true)) {
				z.Write(data, 0, data.Length);
			}
			return output.ToArray();
		}

		public override bool CanRead => _inner.CanRead;
		public override bool CanSeek => false;
		public override bool CanWrite => _inner.CanWrite;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => _inner.Flush();
		public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
		protected override void Dispose(bool disposing) {
			if (disposing) _inner.Dispose();
			base.Dispose(disposing);
		}
	}
}
