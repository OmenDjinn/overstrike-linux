// Reads a toc with Overstrike's DAT1 code and checks asset bytes against expected.json.
// usage: ReadCheck <game dir> <expected.json> <span:assetIdHex=expectedKey>...
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;

var asm = typeof(DAT1.TOC_I29).Assembly;
NativeLibrary.SetDllImportResolver(asm, (name, a, p) =>
    name == "libdeflate.dll" ? NativeLibrary.Load(Path.Combine(Path.GetDirectoryName(asm.Location), "libdeflate_gdeflate.so")) : IntPtr.Zero);

var game = args[0];
var expected = JObject.Parse(File.ReadAllText(args[1]));
var toc = new DAT1.TOC_I29();
if (!toc.Load(Path.Combine(game, "toc"))) { Console.WriteLine("FAIL: toc didn't load"); return 1; }
Console.WriteLine($"toc: {toc.GetArchivesCount()} archives, {toc.AssetIdsSection.Ids.Count} assets");
for (uint i = 0; i < toc.GetArchivesCount(); ++i) Console.WriteLine($"  archive {i}: {toc.GetArchiveFilename(i)}");

var failed = 0;
foreach (var spec in args.Skip(2)) {
	var parts = spec.Split('=');
	var loc = parts[0].Split(':');
	var span = byte.Parse(loc[0]);
	var id = ulong.Parse(loc[1], System.Globalization.NumberStyles.HexNumber);
	var want = Convert.FromHexString((string)expected[parts[1]]);
	var skip = parts.Length > 2 ? int.Parse(parts[2]) : 0;
	want = want.Skip(skip).ToArray();
	byte[] got;
	try { got = toc.GetAssetBytes(span, id); } catch (Exception e) { Console.WriteLine($"FAIL {spec}: {e.GetType().Name}: {e.Message}"); ++failed; continue; }
	var ok = got != null && got.SequenceEqual(want);
	Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {span}/{id:X16}: {got?.Length} bytes (expected {want.Length})");
	if (!ok) ++failed;
}
return failed == 0 ? 0 : 1;
