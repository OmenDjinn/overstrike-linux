// overstrike-cli -- Linux command-line port of Overstrike.
// Licensed under GPLv3, same as Overstrike. See LICENSE.txt.
//
// "overstrike-cli api ..." -- a small JSON interface for the GUI (overstrike-gui).
// Every action prints one JSON object to stdout. Errors go to stderr with exit code 1.

using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Overstrike.Data;
using Overstrike.Games;
using Overstrike.Installers;
using Overstrike.Utils;

namespace OverstrikeCli {
	internal static partial class Program {
		static int CmdApi(List<string> args, string profileOverride) {
			if (args.Count == 0) throw new CliError("Usage: api state|games|apply|options|set-options|toc-status|remove|settings");
			var action = args[0];
			var rest = args.Skip(1).ToList();
			JToken result = action switch {
				"state" => ApiState(profileOverride),
				"games" => ApiGames(),
				"apply" => ApiApply(profileOverride),
				"options" => ApiOptions(rest, profileOverride, set: false),
				"set-options" => ApiOptions(rest, profileOverride, set: true),
				"toc-status" => ApiTocStatus(profileOverride),
				"remove" => ApiRemove(rest),
				"settings" => ApiSettings(profileOverride),
				_ => throw new CliError($"Unknown api action '{action}'."),
			};
			Console.WriteLine(result.ToString(Formatting.None));
			return 0;
		}

		static JObject ApiState(string profileOverride) {
			var profiles = LoadProfiles();
			Profile current = null;
			try { current = CurrentProfile(profileOverride); } catch {}

			var state = new JObject {
				["home"] = Home,
				["library"] = LibraryPath,
				["scriptsProxyPresent"] = File.Exists(Path.Combine(Home, "scripts_proxy.dll")),
				["profiles"] = new JArray(profiles.Select(p => new JObject {
					["name"] = p.Name,
					["game"] = p.Game,
					["gameName"] = GameBase.GetGame(p.Game)?.UserFriendlyName ?? p.Game,
					["path"] = p.GamePath,
				})),
				["current"] = null,
			};
			if (current == null) return state;

			var game = GameBase.GetGame(current.Game);
			var list = BuildModsList(current, out var warnings, out var skipped);
			var tocPath = game.GetTocPath(current.GamePath);
			state["current"] = new JObject {
				["name"] = current.Name,
				["game"] = current.Game,
				["gameName"] = game.UserFriendlyName,
				["path"] = current.GamePath,
				["gameFound"] = Directory.Exists(current.GamePath) && File.Exists(tocPath),
				["modsInstalled"] = IsModded(tocPath),
				["hasScripts"] = game.HasScriptsSettingsSection,
				["scripts"] = current.Settings_Scripts_Enabled,
				["commandline"] = current.Settings_Scripts_CommandLine,
				["language"] = current.Settings_Suit_Language,
				["skipped"] = skipped,
				["languages"] = SuitLanguages(current.Game),
				["warnings"] = new JArray(warnings),
				["mods"] = new JArray(list.Select(m => new JObject {
					["path"] = m.Path,
					["name"] = m.Name,
					["type"] = TypeLabel(m.Type),
					["install"] = m.Install,
					["modular"] = ModEntry.IsTypeFamilyModular(m.Type),
					["suitsMenu"] = m.Type == ModEntry.ModType.SUITS_MENU,
				})),
			};
			return state;
		}

		// same list the GUI's settings tab shows (MainWindow.MakeSuitLanguageItems)
		static readonly Dictionary<string, string> LanguageNames = new() {
			{"us", "English"}, {"uk", "English (UK)"}, {"da", "Danish"}, {"nl", "Dutch"}, {"fi", "Finnish"},
			{"fr", "French"}, {"de", "German"}, {"it", "Italian"}, {"jp", "Japanese"}, {"ko", "Korean"},
			{"no", "Norwegian"}, {"pl", "Polish"}, {"pt", "Brazilian"}, {"ru", "Russian"}, {"es", "Spanish"},
			{"sv", "Swedish"}, {"br", "Portuguese"}, {"ar", "Arabic"}, {"la", "Latin Spanish"},
			{"zh_s", "Simplified Chinese"}, {"zh", "Traditional Chinese"}, {"cs", "Czech"}, {"hu", "Hungarian"}, {"el", "Greek"},
		};

		static JArray SuitLanguages(string gameId) {
			IEnumerable<string> codes = gameId switch {
				GameMSMR.ID => MSMRSuitInstaller.LANGUAGES.Keys,
				GameMM.ID => MMSuit1Installer.LANGUAGES.Keys,
				GameMSM2.ID => MSM2Suit2Installer.LANGUAGES.Keys,
				_ => Array.Empty<string>(),
			};
			var result = new JArray(codes.Select(c => new JObject { ["code"] = c, ["name"] = LanguageNames.GetValueOrDefault(c, c) }));
			result.Add(new JObject { ["code"] = "", ["name"] = "<don't install names>" });
			return result;
		}

		// mods are in the game when 'toc' differs from the clean backup Overstrike made
		static bool IsModded(string tocPath) {
			try {
				var bak = new FileInfo(tocPath + ".BAK");
				var toc = new FileInfo(tocPath);
				if (!bak.Exists || !toc.Exists) return false;
				if (bak.Length != toc.Length) return true;
				return !File.ReadAllBytes(tocPath).AsSpan().SequenceEqual(File.ReadAllBytes(bak.FullName));
			} catch { return false; }
		}

		static JArray ApiGames() =>
			new(FindInstalledGames().Select(g => new JObject {
				["id"] = g.gameId,
				["name"] = GameBase.GetGame(g.gameId).UserFriendlyName,
				["path"] = g.path,
			}));

		// stdin: {"mods":[{"path":"...","install":true}, ...]} -- the new order and enabled states
		static JObject ApiApply(string profileOverride) {
			var input = JObject.Parse(Console.In.ReadToEnd());
			var profile = CurrentProfile(profileOverride);
			var list = BuildModsList(profile, out _, out _);
			var byPath = list.ToDictionary(m => m.Path);

			var result = new List<ModEntry>();
			foreach (var item in (JArray)input["mods"]) {
				var path = (string)item["path"];
				if (!byPath.TryGetValue(path, out var mod)) continue;
				byPath.Remove(path);
				result.Add(new ModEntry(mod, (bool)item["install"], result.Count + 1, mod.Extras));
			}
			result.AddRange(byPath.Values); // anything the GUI didn't know about keeps its place at the end
			KeepSuitsMenuLast(result);
			SaveModsList(profile, result);
			return new JObject { ["ok"] = true };
		}

		// options PATH            -> current choices
		// set-options PATH i=j... -> module i (0-based) gets option j (0-based)
		static JObject ApiOptions(List<string> args, string profileOverride, bool set) {
			if (args.Count == 0) throw new CliError("Usage: api options PATH");
			var profile = CurrentProfile(profileOverride);
			var list = BuildModsList(profile, out _, out _);
			var index = list.FindIndex(m => m.Path == args[0]);
			if (index < 0) throw new CliError("Mod not found.");
			var mod = list[index];
			if (!ModEntry.IsTypeFamilyModular(mod.Type)) throw new CliError("Not a .modular mod.");

			var modules = new List<(string Name, List<string> Options)>();
			string description = null;
			using (var zip = ModularInstaller.ReadModularFile(mod)) {
				var info = ModularInstaller.GetInfo(zip);
				description = (string)info["description"];
				foreach (var entry in (JArray)info["layout"]) {
					if ((string)entry[0] != "module") continue;
					var options = (JArray)entry[2];
					if (options.Count == 1) continue;
					modules.Add(((string)entry[1], options.Select(o => (string)o[1]).ToList()));
				}
			}

			var selected = new int[modules.Count];
			var n = ModularInstaller.GetSelectedCombinationNumber(mod) - 1;
			for (var i = 0; i < modules.Count; ++i) {
				selected[i] = (int)(n % (ulong)modules[i].Options.Count);
				n /= (ulong)modules[i].Options.Count;
			}

			if (set) {
				foreach (var spec in args.Skip(1)) {
					var parts = spec.Split('=');
					var m = int.Parse(parts[0]);
					var o = int.Parse(parts[1]);
					if (m < 0 || m >= modules.Count || o < 0 || o >= modules[m].Options.Count) throw new CliError($"Bad option '{spec}'.");
					selected[m] = o;
				}
				ulong number = 0;
				for (var i = modules.Count - 1; i >= 0; --i) number = number * (ulong)modules[i].Options.Count + (ulong)selected[i];
				var extras = mod.Extras ?? new JObject();
				extras["selections"] = number + 1;
				list[index] = new ModEntry(mod, mod.Install, mod.Order, extras);
				SaveModsList(profile, list);
			}

			return new JObject {
				["name"] = mod.Name,
				["description"] = description,
				["modules"] = new JArray(modules.Select((m, i) => new JObject {
					["name"] = m.Name,
					["options"] = new JArray(m.Options),
					["selected"] = selected[i],
				})),
			};
		}

		static JObject ApiTocStatus(string profileOverride) {
			var profile = CurrentProfile(profileOverride);
			var game = GameBase.GetGame(profile.Game);
			var tocPath = game.GetTocPath(profile.GamePath);
			var shaPath = tocPath + ".sha1";
			var bakPath = tocPath + ".BAK";
			var result = new JObject { ["updated"] = false, ["tocExists"] = File.Exists(tocPath) };
			if (!File.Exists(shaPath) || !File.Exists(tocPath) || !File.Exists(bakPath)) return result;

			var currentSha = Hashes.GetFileSha1(tocPath);
			var remembered = File.ReadAllText(shaPath).Trim();
			if (remembered.Length < currentSha.Length || currentSha.Equals(remembered[..currentSha.Length], StringComparison.OrdinalIgnoreCase)) return result;
			var backupSha = Hashes.GetFileSha1(bakPath);
			if (currentSha.Equals(backupSha, StringComparison.OrdinalIgnoreCase)) return result;

			result["updated"] = true;
			result["tocVersion"] = game.GetTocHashFriendlyName(currentSha);
			result["bakVersion"] = game.GetTocHashFriendlyName(backupSha);
			result["tocDate"] = File.GetLastWriteTime(tocPath).ToString("yyyy-MM-dd HH:mm");
			result["bakDate"] = File.GetLastWriteTime(bakPath).ToString("yyyy-MM-dd HH:mm");
			return result;
		}

		// deletes library files; for mods inside an archive, the whole archive is deleted
		static JObject ApiRemove(List<string> paths) {
			var libraryFull = Path.GetFullPath(LibraryPath) + Path.DirectorySeparatorChar;
			var removed = new JArray();
			foreach (var p in paths) {
				var outer = p.Contains("||") ? p[..p.IndexOf("||")] : p;
				var full = Path.GetFullPath(Path.Combine(LibraryPath, outer));
				if (!full.StartsWith(libraryFull)) throw new CliError($"'{p}' is outside the Mods Library.");
				if (File.Exists(full)) { File.Delete(full); removed.Add(outer); }
			}
			return new JObject { ["removed"] = removed };
		}

		// stdin: {"scripts":bool, "commandline":bool, "language":"us"} (any subset)
		static JObject ApiSettings(string profileOverride) {
			var input = JObject.Parse(Console.In.ReadToEnd());
			var profile = CurrentProfile(profileOverride);
			if (input["scripts"] != null) profile.Settings_Scripts_Enabled = (bool)input["scripts"];
			if (input["commandline"] != null) profile.Settings_Scripts_CommandLine = (bool)input["commandline"];
			if (input["language"] != null) profile.Settings_Suit_Language = (string)input["language"];
			if (!profile.Settings_Scripts_Enabled) profile.Settings_Scripts_CommandLine = false;
			if (!profile.Save()) throw new CliError("Failed to save profile.");
			return new JObject { ["ok"] = true };
		}
	}
}
