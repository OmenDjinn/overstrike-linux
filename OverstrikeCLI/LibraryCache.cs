// overstrike-cli -- Linux command-line port of Overstrike.
// Licensed under GPLv3, same as Overstrike. See LICENSE.txt.
//
// Remembers what mod detection found in each Mods Library file, so unchanged files aren't
// decompressed again. Every GUI refresh scans the whole library, and without this a
// few big .zip/.rar mods made each scan take many seconds.

using System.IO;
using System.IO.Hashing;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Overstrike.Data;
using Overstrike.Detectors;

namespace OverstrikeCli {
	internal class CachedModsDetection: ModsDetection {
		const int FORMAT = 1;
		const int HEAD_LENGTH = 4096;

		readonly string _cachePath;
		readonly string _version;
		Dictionary<string, Entry> _cache = new();
		readonly Dictionary<string, Entry> _seen = new();

		record Entry(long Length, long MTime, uint Head, List<(string Name, string Path, ModEntry.ModType Type)> Mods);

		public CachedModsDetection(string cachePath, string version) {
			_cachePath = cachePath;
			_version = version;
		}

		// full scan: entries for files that are gone get dropped
		public override void Detect(string path, List<ModEntry> mods, List<string> warnings) {
			Load();
			base.Detect(path, mods, warnings);
			Save(_seen);
		}

		public override void DetectInFiles(string basepath, List<string> filenames, List<ModEntry> mods, List<string> warnings) {
			Load();
			base.DetectInFiles(basepath, filenames, mods, warnings);
			foreach (var (k, v) in _seen) _cache[k] = v;
			Save(_cache);
		}

		internal override void Detect(DetectorBase detector, FileStream stream, string relativePath, List<ModEntry> mods, List<string> warnings) {
			var length = stream.Length;
			var mtime = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
			var head = Head(stream);

			if (_cache.TryGetValue(relativePath, out var cached) && cached.Length == length && cached.MTime == mtime && cached.Head == head) {
				foreach (var m in cached.Mods) mods.Add(new ModEntry(m.Name, m.Path, m.Type));
				_seen[relativePath] = cached;
				return;
			}

			var modsBefore = mods.Count;
			var warningsBefore = warnings.Count;
			base.Detect(detector, stream, relativePath, mods, warnings);
			if (warnings.Count > warningsBefore) return; // detect again next time, so the warning shows again

			var produced = mods.Skip(modsBefore).Select(m => (m.Name, m.Path, m.Type)).ToList();
			_seen[relativePath] = new Entry(length, mtime, head, produced);
		}

		static uint Head(FileStream stream) {
			var buffer = new byte[HEAD_LENGTH];
			stream.Position = 0;
			var read = stream.ReadAtLeast(buffer, HEAD_LENGTH, throwOnEndOfStream: false);
			stream.Position = 0;
			return Crc32.HashToUInt32(buffer.AsSpan(0, read));
		}

		void Load() {
			_cache = new();
			try {
				var json = JObject.Parse(File.ReadAllText(_cachePath));
				if ((int?)json["format"] != FORMAT || (string)json["version"] != _version) return; // detectors may have changed
				foreach (var (file, value) in (JObject)json["files"]) {
					var mods = ((JArray)value["mods"]).Select(m => ((string)m[0], (string)m[1], Enum.Parse<ModEntry.ModType>((string)m[2]))).ToList();
					_cache[file] = new Entry((long)value["length"], (long)value["mtime"], (uint)value["head"], mods);
				}
			} catch {
				_cache = new(); // missing or broken cache: just detect everything
			}
		}

		void Save(Dictionary<string, Entry> entries) {
			try {
				var files = new JObject();
				foreach (var (file, e) in entries) {
					files[file] = new JObject {
						["length"] = e.Length,
						["mtime"] = e.MTime,
						["head"] = e.Head,
						["mods"] = new JArray(e.Mods.Select(m => new JArray(m.Name, m.Path, m.Type.ToString()))),
					};
				}
				var json = new JObject { ["format"] = FORMAT, ["version"] = _version, ["files"] = files };
				var tmp = _cachePath + ".tmp";
				File.WriteAllText(tmp, json.ToString(Formatting.None));
				File.Move(tmp, _cachePath, overwrite: true);
			} catch {} // the cache is only an optimization
		}
	}
}
