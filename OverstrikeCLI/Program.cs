// overstrike-cli -- Linux command-line port of Overstrike.
// Overstrike is an open-source mod manager for PC ports of Insomniac Games' games, by Tkachov.
// This program is free software, licensed under GPLv3 like Overstrike itself. See LICENSE.txt.
//
// This file is the command-line front end. All the actual mod-installing logic is Overstrike's
// own code (compiled in unchanged apart from a few small Linux path fixes).

using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Overstrike;
using Overstrike.Data;
using Overstrike.Detectors;
using Overstrike.Games;
using Overstrike.MetaInstallers;
using Overstrike.Utils;

namespace OverstrikeCli {
	internal static partial class Program {
		const string VERSION = "0.2.0";

		static string Home;
		static string LibraryPath => Path.Combine(Home, "Mods Library");
		static string ProfilesPath => Path.Combine(Home, "Profiles");
		static string SettingsPath => Path.Combine(Home, "settings.json");
		static AppSettings Settings;
		static bool Verbose;

		static int Main(string[] argv) {
			RegisterNativeLibraries();

			var args = new List<string>(argv);
			string profileOverride = TakeOption(args, "--profile", "-p");
			Home = Path.GetFullPath(ExpandHome(TakeOption(args, "--home") ?? Environment.GetEnvironmentVariable("OVERSTRIKE_HOME") ?? DefaultHome()));
			bool assumeYes = TakeFlag(args, "--yes", "-y");
			Verbose = TakeFlag(args, "--verbose", "-v");
			if (Verbose) ErrorLogger.Echo = text => Console.Write(text);

			if (args.Count == 0 || args[0] is "help" or "-h" or "--help") { PrintHelp(); return 0; }
			if (args[0] is "version" or "--version") { Console.WriteLine($"overstrike-cli {VERSION}"); return 0; }

			try {
				Directory.CreateDirectory(LibraryPath);
				Directory.CreateDirectory(ProfilesPath);
				Directory.SetCurrentDirectory(Home); // Overstrike's code resolves 'Mods Library', 'Profiles' and logs relative to its working dir
				Settings = File.Exists(SettingsPath) ? new AppSettings(SettingsPath) : new AppSettings();
			} catch (Exception ex) {
				return Fail($"Couldn't prepare the data folder '{Home}': {ex.Message}");
			}

			var cmd = args[0].ToLowerInvariant();
			var rest = args.Skip(1).ToList();
			try {
				switch (cmd) {
					case "setup": return CmdSetup();
					case "games": return CmdGames();
					case "profile":
					case "profiles": return CmdProfile(rest);
					case "add": return CmdAdd(rest);
					case "list":
					case "ls": return CmdList(profileOverride);
					case "enable": return CmdEnable(rest, true, profileOverride);
					case "disable": return CmdEnable(rest, false, profileOverride);
					case "move": return CmdMove(rest, profileOverride);
					case "options": return CmdOptions(rest, profileOverride);
					case "install": return CmdInstall(profileOverride, rest, assumeYes);
					case "uninstall": return CmdUninstall(profileOverride);
					case "scripts": return CmdScripts(rest, profileOverride);
					case "language": return CmdLanguage(rest, profileOverride);
					case "reset-toc": return CmdResetToc(profileOverride, assumeYes);
					case "launch-options": return CmdLaunchOptions(profileOverride);
					case "where":
					case "paths": return CmdWhere(profileOverride);
					case "api": return CmdApi(rest, profileOverride); // machine-readable interface used by the GUI
					default:
						return Fail($"Unknown command '{args[0]}'. Run 'overstrike-cli help' for the list of commands.");
				}
			} catch (CliError e) {
				return Fail(e.Message);
			}
		}

		#region help

		static void PrintHelp() {
			Console.WriteLine($@"overstrike-cli {VERSION} -- Overstrike mod manager for Linux (command line)
Installs .smpcmod/.suit/.stage/.modular/.script mods for Insomniac PC ports
(Marvel's Spider-Man 2, Remastered, Miles Morales, Ratchet & Clank) using Overstrike's own code.

Usage: overstrike-cli [--profile NAME] [--home DIR] [-y] <command> [args]

Getting started:
  setup                     Find your Steam games and create a profile for each
  add FILE...               Copy mod files (or .zip/.7z/.rar with mods inside) into the Mods Library
  list                      Show mods for the current profile, in load order
  install                   Install all enabled mods into the game

Managing mods:
  enable N... | all         Enable mods by number (from 'list') or by name
  disable N... | all        Disable mods
  move N TO                 Move mod #N to position TO in the load order
  options N [M=O ...]       Show or pick options of a .modular mod (module M gets option O)
  uninstall                 Remove all mods (restore the game's original 'toc')
  scripts on|off [--commandline]
                            Enable .script mods (needs scripts_proxy.dll, see 'where')
  language CODE             Suit name language (us, fr, de, ...; default us)

Profiles and games:
  games                     List supported games found in Steam libraries
  profile list              List profiles (* = current)
  profile create NAME [GAME_PATH]
  profile use NAME          Switch the current profile
  profile delete NAME
  where                     Show data folder, Mods Library and game paths

Troubleshooting:
  reset-toc                 Delete 'toc', 'toc.BAK' and the game .exe so Steam re-downloads
                            clean copies (use after a game update breaks mods), then verify files
  launch-options            Print the Steam launch options to use with Proton

Options:
  -p, --profile NAME        Use this profile instead of the current one
  --home DIR                Data folder (default: ~/.local/share/overstrike-cli, or $OVERSTRIKE_HOME)
                            Pointing it at a Windows Overstrike folder reuses its Mods Library and Profiles.
  -y, --yes                 Don't ask for confirmation
  -v, --verbose             Print Overstrike's detailed install log
  install --update-backup | --keep-backup
                            Answer the 'game was updated' question without prompting");
		}

		#endregion
		#region setup / games / profiles

		static int CmdSetup() {
			Console.WriteLine($"Data folder: {Home}");
			var found = FindInstalledGames();
			if (found.Count == 0) {
				Console.WriteLine("No supported games found in your Steam libraries.");
				Console.WriteLine("Create a profile manually: overstrike-cli profile create NAME \"/path/to/game\"");
				return 0;
			}

			var existing = LoadProfiles();
			foreach (var (gameId, path) in found) {
				var game = GameBase.GetGame(gameId);
				var already = existing.FirstOrDefault(p => p.Game == gameId && SamePath(p.GamePath, path));
				if (already != null) {
					Console.WriteLine($"  {game.UserFriendlyName}: profile '{already.Name}' already exists");
					if (Settings.CurrentProfile == null) Settings.CurrentProfile = already.Name;
					continue;
				}
				var name = UniqueProfileName(game.UserFriendlyName, existing);
				var profile = NewProfile(name, gameId, path);
				existing.Add(profile);
				Console.WriteLine($"  {game.UserFriendlyName}: created profile '{name}'\n      {path}");
				if (Settings.CurrentProfile == null || gameId == GameMSM2.ID) {
					Settings.CurrentProfile = name;
				}
			}
			SaveSettings();
			Console.WriteLine($"\nCurrent profile: {Settings.CurrentProfile}");
			Console.WriteLine("Next: overstrike-cli add ~/Downloads/some_mod.suit   then   overstrike-cli install");
			return 0;
		}

		static int CmdGames() {
			var found = FindInstalledGames();
			if (found.Count == 0) {
				Console.WriteLine("No supported games found in Steam libraries.");
				return 0;
			}
			foreach (var (gameId, path) in found) {
				Console.WriteLine($"{GameBase.GetGame(gameId).UserFriendlyName} [{gameId}]\n    {path}");
			}
			return 0;
		}

		static int CmdProfile(List<string> args) {
			var sub = args.Count > 0 ? args[0].ToLowerInvariant() : "list";
			var profiles = LoadProfiles();
			switch (sub) {
				case "list":
				case "ls":
					if (profiles.Count == 0) { Console.WriteLine("No profiles yet. Run 'overstrike-cli setup'."); return 0; }
					foreach (var p in profiles) {
						var mark = p.Name == Settings.CurrentProfile ? "*" : " ";
						var game = GameBase.GetGame(p.Game)?.UserFriendlyName ?? p.Game;
						Console.WriteLine($"{mark} {p.Name}  ({game})\n    {p.GamePath}");
					}
					return 0;

				case "create": {
					if (args.Count < 2) throw new CliError("Usage: profile create NAME [GAME_PATH]");
					var name = args[1];
					if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new CliError("Profile name contains characters not allowed in file names.");
					if (profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new CliError($"Profile '{name}' already exists.");

					string path;
					if (args.Count >= 3) {
						path = Path.GetFullPath(ExpandHome(args[2]));
					} else {
						var found = FindInstalledGames();
						if (found.Count == 0) throw new CliError("No game found automatically; pass the game folder: profile create NAME \"/path/to/game\"");
						var msm2 = found.Where(g => g.gameId == GameMSM2.ID).ToList();
						path = msm2.Count > 0 ? msm2[0].path : found[0].path;
					}

					var gameId = GameBase.DetectGameInstallation(path);
					if (gameId == null) throw new CliError($"'{path}' doesn't look like a supported game folder (expected 'toc' and the game .exe).");

					NewProfile(name, gameId, path);
					Settings.CurrentProfile = name;
					SaveSettings();
					Console.WriteLine($"Created profile '{name}' for {GameBase.GetGame(gameId).UserFriendlyName} and made it current.");
					return 0;
				}

				case "use":
				case "switch": {
					if (args.Count < 2) throw new CliError("Usage: profile use NAME");
					var p = FindProfile(profiles, args[1]);
					Settings.CurrentProfile = p.Name;
					SaveSettings();
					Console.WriteLine($"Current profile: {p.Name}");
					return 0;
				}

				case "delete":
				case "rm": {
					if (args.Count < 2) throw new CliError("Usage: profile delete NAME");
					var p = FindProfile(profiles, args[1]);
					File.Delete(p.FullPath);
					if (Settings.CurrentProfile == p.Name) { Settings.CurrentProfile = null; SaveSettings(); }
					Console.WriteLine($"Deleted profile '{p.Name}'. Mods already in the game stay until you install or uninstall again.");
					return 0;
				}

				default:
					throw new CliError("Usage: profile list | create NAME [PATH] | use NAME | delete NAME");
			}
		}

		static Profile NewProfile(string name, string gameId, string path) {
			var profile = new Profile(name, gameId, path);
			profile.FullPath = Path.Combine(ProfilesPath, name + ".json");
			if (!profile.Save()) throw new CliError($"Failed to save profile '{name}'.");
			return profile;
		}

		#endregion
		#region mods library

		static int CmdAdd(List<string> files) {
			if (files.Count == 0) throw new CliError("Usage: add FILE...");

			var copied = new List<string>();
			foreach (var f in files) {
				var src = Path.GetFullPath(ExpandHome(f));
				if (!File.Exists(src)) { Console.Error.WriteLine($"Not found: {f}"); continue; }
				var dst = Path.Combine(LibraryPath, Path.GetFileName(src));
				if (Path.GetFullPath(dst) != src) File.Copy(src, dst, true);
				copied.Add(dst);
				Console.WriteLine($"Added {Path.GetFileName(src)}");
			}
			if (copied.Count == 0) return 1;

			// detect right away, so the user learns early if a file isn't a supported mod
			var mods = new List<ModEntry>();
			var warnings = new List<string>();
			new ModsDetection().DetectInFiles(LibraryPath, copied, mods, warnings);
			foreach (var w in warnings) Console.WriteLine($"Warning: {w}");
			if (mods.Count == 0) {
				Console.WriteLine("No supported mods were found in the added file(s).");
				return 1;
			}
			foreach (var m in mods) Console.WriteLine($"  found: {m.Name}  [{TypeLabel(m.Type)}]");
			Console.WriteLine("New mods are enabled by default. Run 'overstrike-cli install' to apply.");
			return 0;
		}

		static int CmdList(string profileOverride) {
			var profile = CurrentProfile(profileOverride);
			var game = GameBase.GetGame(profile.Game);
			var list = BuildModsList(profile, out var warnings, out var skipped);

			Console.WriteLine($"Profile: {profile.Name}  ({game.UserFriendlyName})");
			Console.WriteLine($"Game:    {profile.GamePath}");
			if (game.HasScriptsSettingsSection) {
				Console.WriteLine($"Scripts: {(profile.Settings_Scripts_Enabled ? "on" : "off")}{(profile.Settings_Scripts_Enabled && profile.Settings_Scripts_CommandLine ? " (+commandline.txt)" : "")}");
			}
			Console.WriteLine();
			if (!list.Any(m => m.Type != ModEntry.ModType.SUITS_MENU)) {
				Console.WriteLine("No mods for this game in the library yet. Add some with: overstrike-cli add FILE\n");
			}
			var width = list.Count.ToString().Length;
			for (var i = 0; i < list.Count; ++i) {
				var m = list[i];
				var box = m.Install ? "[x]" : "[ ]";
				Console.WriteLine($"{(i + 1).ToString().PadLeft(width)}. {box} {m.Name}  ({TypeLabel(m.Type)})");
			}
			foreach (var w in warnings) Console.WriteLine($"Warning: {w}");
			if (skipped > 0) Console.WriteLine($"\n({skipped} mod(s) in the library are for other games and are hidden.)");
			return 0;
		}

		static int CmdEnable(List<string> args, bool enable, string profileOverride) {
			if (args.Count == 0) throw new CliError($"Usage: {(enable ? "enable" : "disable")} N... | all");
			var profile = CurrentProfile(profileOverride);
			var list = BuildModsList(profile, out _, out _);

			var targets = args.Count == 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase)
				? Enumerable.Range(0, list.Count).Where(i => list[i].Type != ModEntry.ModType.SUITS_MENU).ToList()
				: ResolveModRefs(list, args);

			foreach (var i in targets) {
				list[i].Install = enable;
				Console.WriteLine($"{(enable ? "Enabled" : "Disabled")}: {list[i].Name}");
			}
			SaveModsList(profile, list);
			Console.WriteLine("Run 'overstrike-cli install' to apply.");
			return 0;
		}

		static int CmdMove(List<string> args, string profileOverride) {
			if (args.Count != 2) throw new CliError("Usage: move N TO");
			var profile = CurrentProfile(profileOverride);
			var list = BuildModsList(profile, out _, out _);
			var from = ResolveModRefs(list, new List<string> { args[0] })[0];
			if (!int.TryParse(args[1], out var to) || to < 1 || to > list.Count) throw new CliError($"TO must be a number from 1 to {list.Count}.");
			var mod = list[from];
			list.RemoveAt(from);
			list.Insert(to - 1, mod);
			KeepSuitsMenuLast(list);
			SaveModsList(profile, list);
			Console.WriteLine($"Moved '{mod.Name}' to position {list.IndexOf(mod) + 1}. Mods lower in the list win when they change the same files.");
			Console.WriteLine("Run 'overstrike-cli install' to apply.");
			return 0;
		}

		// .modular mods: same encoding as the GUI's ModularWizard ("selections" = 1 + mixed-radix index
		// over the modules that have more than one option, first module = lowest digit)
		static int CmdOptions(List<string> args, string profileOverride) {
			if (args.Count == 0) throw new CliError("Usage: options N [MODULE=OPTION ...]");
			var profile = CurrentProfile(profileOverride);
			var list = BuildModsList(profile, out _, out _);
			var index = ResolveModRefs(list, new List<string> { args[0] })[0];
			var mod = list[index];
			if (!ModEntry.IsTypeFamilyModular(mod.Type)) throw new CliError($"'{mod.Name}' is not a .modular mod, it has no options.");

			var modules = new List<(string Name, List<string> Options)>();
			using (var zip = Overstrike.Installers.ModularInstaller.ReadModularFile(mod)) {
				var info = Overstrike.Installers.ModularInstaller.GetInfo(zip);
				foreach (var entry in (Newtonsoft.Json.Linq.JArray)info["layout"]) {
					if ((string)entry[0] != "module") continue;
					var options = (Newtonsoft.Json.Linq.JArray)entry[2];
					if (options.Count == 1) continue;
					modules.Add(((string)entry[1], options.Select(o => (string)o[1]).ToList()));
				}
			}
			if (modules.Count == 0) { Console.WriteLine($"'{mod.Name}' has no choices to make."); return 0; }

			var selected = new int[modules.Count];
			var n = Overstrike.Installers.ModularInstaller.GetSelectedCombinationNumber(mod) - 1;
			for (var i = 0; i < modules.Count; ++i) {
				selected[i] = (int)(n % (ulong)modules[i].Options.Count);
				n /= (ulong)modules[i].Options.Count;
			}

			foreach (var spec in args.Skip(1)) {
				var eq = spec.IndexOf('=');
				if (eq <= 0) throw new CliError($"Expected MODULE=OPTION, got '{spec}'.");
				var m = PickIndex(spec[..eq], modules.Select(x => x.Name).ToList(), "module");
				var o = PickIndex(spec[(eq + 1)..], modules[m].Options, $"option of '{modules[m].Name}'");
				selected[m] = o;
			}

			if (args.Count > 1) {
				ulong number = 0;
				for (var i = modules.Count - 1; i >= 0; --i) {
					number = number * (ulong)modules[i].Options.Count + (ulong)selected[i];
				}
				number += 1;
				var extras = mod.Extras ?? new Newtonsoft.Json.Linq.JObject();
				extras["selections"] = number;
				list[index] = new ModEntry(mod, mod.Install, mod.Order, extras);
				SaveModsList(profile, list);
			}

			Console.WriteLine($"{mod.Name}");
			for (var i = 0; i < modules.Count; ++i) {
				Console.WriteLine($"  {i + 1}. {modules[i].Name}");
				for (var j = 0; j < modules[i].Options.Count; ++j) {
					Console.WriteLine($"       {(selected[i] == j ? "(*)" : "( )")} {j + 1}. {modules[i].Options[j]}");
				}
			}
			if (args.Count > 1) Console.WriteLine("\nSaved. Run 'overstrike-cli install' to apply.");
			else Console.WriteLine($"\nChange with e.g.: overstrike-cli options {index + 1} 1=2   (module 1 gets option 2)");
			return 0;
		}

		static int PickIndex(string text, List<string> names, string what) {
			if (int.TryParse(text, out var n)) {
				if (n < 1 || n > names.Count) throw new CliError($"No {what} #{n} (1-{names.Count}).");
				return n - 1;
			}
			var exact = names.FindIndex(x => x.Equals(text, StringComparison.OrdinalIgnoreCase));
			if (exact >= 0) return exact;
			var matches = Enumerable.Range(0, names.Count).Where(i => names[i].Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
			if (matches.Count != 1) throw new CliError(matches.Count == 0 ? $"No {what} matches '{text}'." : $"'{text}' matches several {what}s; use the number.");
			return matches[0];
		}

		#endregion
		#region install / uninstall

		static int CmdInstall(string profileOverride, List<string> args, bool assumeYes) {
			var updateBackup = args.Contains("--update-backup");
			var keepBackup = args.Contains("--keep-backup");

			var profile = CurrentProfile(profileOverride);
			var game = GameBase.GetGame(profile.Game);
			CheckGameFolder(profile, game);
			WarnIfGameRunning(game, profile.GamePath);

			if (!HandleTocUpdateCheck(profile, game, updateBackup, keepBackup, assumeYes)) return 1;

			var list = BuildModsList(profile, out var warnings, out _);
			foreach (var w in warnings) Console.WriteLine($"Warning: {w}");
			SaveModsList(profile, list);

			if (profile.Settings_Scripts_Enabled && !File.Exists(Path.Combine(Home, "scripts_proxy.dll"))) {
				throw new CliError(
					"Scripts are enabled, but 'scripts_proxy.dll' is missing from the data folder.\n" +
					$"Copy it from the Windows Overstrike download (it's next to Overstrike.exe) into:\n  {Home}\n" +
					"or turn scripts off: overstrike-cli scripts off");
			}

			// collect (expands .modular mods into their selected options, adds the script support entry)
			List<ModEntry> toInstall = null;
			Exception collectError = null;
			string collectFailedOn = null;
			var libraryMods = list.Where(m => m.Type != ModEntry.ModType.SUITS_MENU).ToList();
			var collector = new ModCollectingThreadBuilder(profile, libraryMods) {
				OnException = (name, ex) => { collectFailedOn = name; collectError = ex; },
				OnSuccess = mods => toInstall = mods,
			};
			RunThread(collector.Build());
			if (collectError != null) throw new CliError($"Failed to read mod '{collectFailedOn}': {collectError.Message}");
			if (toInstall == null) throw new CliError("Failed to collect mods to install.");

			var count = toInstall.Count(m => m.Type != ModEntry.ModType.SUITS_MENU && m.Type != ModEntry.ModType.SCRIPT_SUPPORT);
			Console.WriteLine($"Installing {count} mod(s) into {game.UserFriendlyName}...");

			if (!RunInstaller(profile, toInstall, uninstalling: false)) return 1;

			Console.WriteLine("\nDone! Start the game from Steam as usual.");
			if (profile.Settings_Scripts_Enabled) {
				Console.WriteLine("Scripts are on, so set these Steam launch options:");
				Console.WriteLine(profile.Settings_Scripts_CommandLine ? "  WINEDLLOVERRIDES=\"winmm=n,b\" %command%" : "  WINEDLLOVERRIDES=\"winmm=n,b\" %command% -scripts");
			}
			return 0;
		}

		static int CmdUninstall(string profileOverride) {
			var profile = CurrentProfile(profileOverride);
			var game = GameBase.GetGame(profile.Game);
			CheckGameFolder(profile, game);
			WarnIfGameRunning(game, profile.GamePath);

			var bak = game.GetTocPath(profile.GamePath) + ".BAK";
			if (!File.Exists(bak)) {
				Console.WriteLine("There's no 'toc.BAK', so mods were never installed by Overstrike here (or the backup was removed). Nothing to restore.");
				return 0;
			}

			Console.WriteLine($"Restoring original 'toc' for {game.UserFriendlyName}...");
			if (!RunInstaller(profile, new List<ModEntry>(), uninstalling: true)) return 1;
			Console.WriteLine("Done. The game is back to vanilla.");
			return 0;
		}

		static bool RunInstaller(Profile profile, List<ModEntry> mods, bool uninstalling) {
			var failed = false;
			var builder = new ModInstallingThreadBuilder(Settings, profile, mods, uninstalling) {
				OnErrorOccurred_BeforeWritingTrace = () => failed = true,
			};
			if (!Verbose) { // in verbose mode Overstrike's own log already shows progress
				builder.OnOperationStarts = (i, total, name) => Console.WriteLine($"  [{i + 1}/{total}] {name}");
				builder.OnOperationsFinalizing = (_, _) => Console.WriteLine("  Saving 'toc'...");
			}

			RunThread(builder.Build());

			if (failed) {
				var log = Path.Combine(Home, "errors.log");
				Console.Error.WriteLine($"\nSomething went wrong. Details were written to:\n  {log}");
				try {
					var lines = File.ReadAllLines(log);
					var start = Array.FindLastIndex(lines, l => l.Contains("Error occurred"));
					if (start >= 0) {
						Console.Error.WriteLine();
						foreach (var l in lines.Skip(start).Take(12)) Console.Error.WriteLine("  " + l);
					}
				} catch {}
				Console.Error.WriteLine("\nIf the game was updated recently, run 'overstrike-cli reset-toc', verify files in Steam, then install again.");
			}
			return !failed;
		}

		// Mirrors the GUI's TocMismatchDialog: after a game update, 'toc' differs from what we last wrote,
		// and 'toc.BAK' still holds the old game's toc. Installing on top of a stale backup breaks the game.
		static bool HandleTocUpdateCheck(Profile profile, GameBase game, bool updateBackup, bool keepBackup, bool assumeYes) {
			var tocPath = game.GetTocPath(profile.GamePath);
			var shaFilePath = tocPath + ".sha1";
			var tocBakPath = tocPath + ".BAK";
			if (!File.Exists(shaFilePath) || !File.Exists(tocPath) || !File.Exists(tocBakPath)) return true;

			var currentSha = Hashes.GetFileSha1(tocPath);
			var rememberedSha = File.ReadAllText(shaFilePath).Trim();
			if (rememberedSha.Length < currentSha.Length) return true;
			if (currentSha.Equals(rememberedSha[..currentSha.Length], StringComparison.OrdinalIgnoreCase)) return true;

			var backupSha = Hashes.GetFileSha1(tocBakPath);
			if (currentSha.Equals(backupSha, StringComparison.OrdinalIgnoreCase)) return true;

			string Ver(string sha) => game.GetTocHashFriendlyName(sha) is string v ? $" ({v})" : "";
			Console.WriteLine("'toc' has changed since mods were last installed. Most likely the game was updated.");
			Console.WriteLine($"  toc:     {currentSha[..7]}{Ver(currentSha)}  modified {File.GetLastWriteTime(tocPath)}");
			Console.WriteLine($"  toc.BAK: {backupSha[..7]}{Ver(backupSha)}  modified {File.GetLastWriteTime(tocBakPath)}");

			bool update;
			if (updateBackup) update = true;
			else if (keepBackup) update = false;
			else if (assumeYes) update = true;
			else {
				Console.Write("Use the new 'toc' as the clean backup? (recommended after an update) [Y/n/q]: ");
				var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
				if (answer is "q" or "quit" or "c" or "cancel") { Console.WriteLine("Cancelled."); return false; }
				update = answer is "" or "y" or "yes";
			}

			if (update) {
				File.Copy(tocPath, tocBakPath, true);
				Console.WriteLine("Updated 'toc.BAK' from the current 'toc'.\n");
			} else {
				Console.WriteLine("Keeping the existing 'toc.BAK'.\n");
			}
			return true;
		}

		#endregion
		#region settings commands

		static int CmdScripts(List<string> args, string profileOverride) {
			var profile = CurrentProfile(profileOverride);
			if (!GameBase.GetGame(profile.Game).HasScriptsSettingsSection) throw new CliError("This game doesn't support .script mods.");
			if (args.Count == 0) {
				Console.WriteLine($"Scripts: {(profile.Settings_Scripts_Enabled ? "on" : "off")}; add -scripts to commandline.txt: {(profile.Settings_Scripts_CommandLine ? "yes" : "no")}");
				return 0;
			}
			var on = args[0].ToLowerInvariant() switch {
				"on" or "enable" or "true" => true,
				"off" or "disable" or "false" => false,
				_ => throw new CliError("Usage: scripts on|off [--commandline]")
			};
			profile.Settings_Scripts_Enabled = on;
			profile.Settings_Scripts_CommandLine = on && args.Contains("--commandline");
			profile.Save();
			Console.WriteLine($"Scripts {(on ? "enabled" : "disabled")} for '{profile.Name}'. Run 'overstrike-cli install' to apply.");
			if (on && !File.Exists(Path.Combine(Home, "scripts_proxy.dll"))) {
				Console.WriteLine($"\nNote: copy 'scripts_proxy.dll' from the Windows Overstrike download into:\n  {Home}");
			}
			return 0;
		}

		static int CmdLanguage(List<string> args, string profileOverride) {
			var profile = CurrentProfile(profileOverride);
			if (args.Count == 0) { Console.WriteLine($"Suit language: {profile.Settings_Suit_Language}"); return 0; }
			profile.Settings_Suit_Language = args[0].ToLowerInvariant();
			profile.Save();
			Console.WriteLine($"Suit language set to '{profile.Settings_Suit_Language}'.");
			return 0;
		}

		static int CmdResetToc(string profileOverride, bool assumeYes) {
			var profile = CurrentProfile(profileOverride);
			var game = GameBase.GetGame(profile.Game);
			var exe = game.GetExecutablePath(profile.GamePath);
			var toc = game.GetTocPath(profile.GamePath);
			var bak = toc + ".BAK";

			Console.WriteLine("This deletes 'toc', 'toc.BAK' and the game .exe, so Steam re-downloads clean copies:");
			Console.WriteLine($"  {toc}\n  {bak}\n  {exe}");
			if (!assumeYes && !Confirm("Continue?")) { Console.WriteLine("Cancelled."); return 1; }
			if (IsGameRunning(game, profile.GamePath)) throw new CliError("The game seems to be running. Close it first.");

			foreach (var f in new[] { exe, toc, bak, toc + ".sha1" }) {
				try { if (File.Exists(f)) File.Delete(f); } catch (Exception ex) { throw new CliError($"Failed to delete {f}: {ex.Message}"); }
			}
			Console.WriteLine("\nDone. Now in Steam: right-click the game > Properties > Installed Files > Verify integrity of game files.");
			Console.WriteLine("Then run 'overstrike-cli install' again.");
			return 0;
		}

		static int CmdLaunchOptions(string profileOverride) {
			Profile profile = null;
			try { profile = CurrentProfile(profileOverride); } catch {}
			var scripts = profile?.Settings_Scripts_Enabled ?? false;
			var cmdline = profile?.Settings_Scripts_CommandLine ?? false;

			Console.WriteLine("Regular mods (suits, stages, smpcmods, modulars) need no launch options: just start the game from Steam.");
			Console.WriteLine("For .script mods, set these Steam launch options (Properties > General > Launch Options):\n");
			Console.WriteLine(cmdline ? "  WINEDLLOVERRIDES=\"winmm=n,b\" %command%" : "  WINEDLLOVERRIDES=\"winmm=n,b\" %command% -scripts");
			if (profile != null && !scripts) Console.WriteLine("\n(Scripts are currently off for this profile: overstrike-cli scripts on)");
			return 0;
		}

		static int CmdWhere(string profileOverride) {
			Console.WriteLine($"Data folder:  {Home}");
			Console.WriteLine($"Mods Library: {LibraryPath}");
			Console.WriteLine($"Profiles:     {ProfilesPath}");
			Console.WriteLine($"Error log:    {Path.Combine(Home, "errors.log")}");
			var proxy = Path.Combine(Home, "scripts_proxy.dll");
			Console.WriteLine($"Scripts DLL:  {proxy} {(File.Exists(proxy) ? "(present)" : "(missing; only needed for .script mods)")}");
			try {
				var p = CurrentProfile(profileOverride);
				Console.WriteLine($"Profile:      {p.Name} -> {p.GamePath}");
			} catch {}
			return 0;
		}

		#endregion
		#region mods list (same rules as the GUI's MainWindow.MakeModsItems)

		static List<ModEntry> DetectLibrary(out List<string> warnings) {
			var mods = new List<ModEntry>();
			warnings = new List<string>();
			new ModsDetection().Detect(LibraryPath, mods, warnings);
			return mods;
		}

		static List<ModEntry> BuildModsList(Profile profile, out List<string> warnings, out int skipped) {
			var game = GameBase.GetGame(profile.Game);
			var library = DetectLibrary(out warnings);

			var profileInstalled = new Dictionary<string, bool>();
			foreach (var mod in profile.Mods) profileInstalled[mod.Path] = mod.Install;

			var available = new Dictionary<string, ModEntry>();
			skipped = 0;
			foreach (var mod in library) {
				if (!game.IsCompatible(mod)) { ++skipped; continue; }
				available[mod.Path] = mod;
			}

			var list = new List<ModEntry>();
			var index = 1;
			foreach (var mod in profile.Mods) { // previously known mods, in saved order
				if (available.TryGetValue(mod.Path, out var lib)) {
					list.Add(new ModEntry(lib, mod.Install, index++, mod.Extras));
				}
			}
			foreach (var mod in library) { // new mods go to the end
				if (available.ContainsKey(mod.Path) && !profileInstalled.ContainsKey(mod.Path)) {
					list.Add(new ModEntry(available[mod.Path], mod.Install, index++, null));
				}
			}

			if (HasSuitsMenu(profile.Game)) {
				if (!profileInstalled.TryGetValue(ModEntry.SUITS_MENU_PATH, out var suitsMenuInstalled)) suitsMenuInstalled = true;
				var stub = new ModEntry("Suits Menu", ModEntry.SUITS_MENU_PATH, ModEntry.ModType.SUITS_MENU);
				list.Add(new ModEntry(stub, suitsMenuInstalled, index, null));
			}

			KeepSuitsMenuLast(list);
			return list;
		}

		static void SaveModsList(Profile profile, List<ModEntry> list) {
			profile.Mods = list.Select(m => new ModEntry(m.Path, m.Install, m.Extras)).ToList();
			if (!profile.Save()) throw new CliError($"Failed to save profile '{profile.Name}'.");
		}

		static void KeepSuitsMenuLast(List<ModEntry> list) {
			var i = list.FindIndex(m => m.Type == ModEntry.ModType.SUITS_MENU);
			if (i >= 0 && i != list.Count - 1) {
				var menu = list[i];
				list.RemoveAt(i);
				list.Add(menu);
			}
		}

		static bool HasSuitsMenu(string gameId) => gameId == GameMSMR.ID || gameId == GameMM.ID || gameId == GameMSM2.ID;

		static List<int> ResolveModRefs(List<ModEntry> list, List<string> refs) {
			var result = new List<int>();
			foreach (var r in refs) {
				if (int.TryParse(r, out var n)) {
					if (n < 1 || n > list.Count) throw new CliError($"There's no mod #{n} (run 'overstrike-cli list').");
					result.Add(n - 1);
					continue;
				}
				var matches = Enumerable.Range(0, list.Count)
					.Where(i => list[i].Name.Contains(r, StringComparison.OrdinalIgnoreCase) || list[i].Path.Contains(r, StringComparison.OrdinalIgnoreCase))
					.ToList();
				if (matches.Count == 0) throw new CliError($"No mod matches '{r}'.");
				if (matches.Count > 1) throw new CliError($"'{r}' matches several mods: {string.Join(", ", matches.Select(i => $"#{i + 1} {list[i].Name}"))}. Use the number.");
				result.Add(matches[0]);
			}
			return result;
		}

		static string TypeLabel(ModEntry.ModType t) {
			var s = t.ToString();
			if (s.StartsWith("SUITS_MENU")) return "suits menu";
			if (s.StartsWith("SUIT_STYLE")) return "suit style";
			if (s.StartsWith("SUIT")) return "suit";
			if (s.StartsWith("STAGE")) return "stage";
			if (s.StartsWith("MODULAR")) return "modular";
			if (s.StartsWith("SCRIPT")) return "script";
			if (s.StartsWith("SMPC") || s.StartsWith("MMPC")) return "smpcmod";
			return s.ToLowerInvariant();
		}

		#endregion
		#region profiles helpers

		static List<Profile> LoadProfiles() {
			var result = new List<Profile>();
			foreach (var f in Directory.GetFiles(ProfilesPath, "*.json").OrderBy(f => f)) {
				try {
					var p = new Profile(f);
					NormalizeWindowsPaths(p);
					result.Add(p);
				} catch {
					Console.Error.WriteLine($"Warning: skipping unreadable profile {Path.GetFileName(f)}");
				}
			}
			return result;
		}

		// Profiles copied over from Windows Overstrike store mod paths like "folder\mod.suit".
		static void NormalizeWindowsPaths(Profile p) {
			for (var i = 0; i < p.Mods.Count; ++i) {
				var m = p.Mods[i];
				if (m.Path == ModEntry.SUITS_MENU_PATH || !m.Path.Contains('\\')) continue;
				var cut = m.Path.IndexOf("||");
				var outer = cut >= 0 ? m.Path[..cut] : m.Path;
				var inner = cut >= 0 ? m.Path[cut..] : "";
				p.Mods[i] = new ModEntry(outer.Replace('\\', '/') + inner, m.Install, m.Extras);
			}
		}

		static Profile CurrentProfile(string overrideName) {
			var profiles = LoadProfiles();
			if (profiles.Count == 0) throw new CliError("No profiles yet. Run 'overstrike-cli setup' first.");

			var name = overrideName ?? Settings.CurrentProfile;
			if (name == null) {
				if (profiles.Count == 1) return profiles[0];
				throw new CliError("Several profiles exist; pick one with 'overstrike-cli profile use NAME'.");
			}
			return FindProfile(profiles, name);
		}

		static Profile FindProfile(List<Profile> profiles, string name) {
			var p = profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
			if (p == null) throw new CliError($"No profile named '{name}'. Existing: {string.Join(", ", profiles.Select(x => x.Name))}");
			if (GameBase.GetGame(p.Game) == null) throw new CliError($"Profile '{p.Name}' is for an unknown game '{p.Game}'.");
			return p;
		}

		static string UniqueProfileName(string baseName, List<Profile> existing) {
			var bad = Path.GetInvalidFileNameChars();
			var clean = new string(baseName.Where(c => Array.IndexOf(bad, c) < 0 && c != ':').ToArray()).Trim();
			var name = clean;
			for (var i = 2; existing.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); ++i) name = $"{clean} {i}";
			return name;
		}

		static void SaveSettings() => Settings.Save(SettingsPath);

		#endregion
		#region game folder / steam

		static void CheckGameFolder(Profile profile, GameBase game) {
			if (!Directory.Exists(profile.GamePath)) throw new CliError($"Game folder not found: {profile.GamePath}");
			if (!File.Exists(game.GetTocPath(profile.GamePath))) {
				throw new CliError("The game's 'toc' file is missing. In Steam: right-click the game > Properties > Installed Files > Verify integrity of game files.");
			}
			if (!game.IsGameInstallation(profile.GamePath)) {
				throw new CliError($"'{profile.GamePath}' doesn't look like a {game.UserFriendlyName} installation.");
			}
		}

		static List<(string gameId, string path)> FindInstalledGames() {
			var result = new List<(string, string)>();
			var seen = new HashSet<string>();
			foreach (var lib in SteamLibraries()) {
				var common = Path.Combine(lib, "steamapps", "common");
				if (!Directory.Exists(common)) continue;
				foreach (var dir in Directory.GetDirectories(common)) {
					string real;
					try { real = RealPath(dir); } catch { real = dir; }
					if (!seen.Add(real)) continue;
					var id = GameBase.DetectGameInstallation(dir);
					if (id != null) result.Add((id, dir));
				}
			}
			return result;
		}

		static string RealPath(string path) {
			// resolve symlinks in every component (~/.steam/steam is usually a link to ~/.local/share/Steam)
			var full = Path.GetFullPath(path);
			var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
			var current = "/";
			foreach (var part in parts) {
				current = Path.Combine(current, part);
				var info = new DirectoryInfo(current);
				if (info.LinkTarget != null) {
					var target = info.ResolveLinkTarget(true);
					if (target != null) current = target.FullName;
				}
			}
			return current;
		}

		static IEnumerable<string> SteamLibraries() {
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			var roots = new[] {
				Path.Combine(home, ".local/share/Steam"),
				Path.Combine(home, ".steam/steam"),
				Path.Combine(home, ".steam/root"),
				Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam"), // Flatpak
				Path.Combine(home, "snap/steam/common/.local/share/Steam"),               // Snap
			};
			var libs = new List<string>();
			foreach (var root in roots) {
				if (!Directory.Exists(root)) continue;
				libs.Add(root);
				var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
				if (!File.Exists(vdf)) continue;
				try {
					foreach (var line in File.ReadAllLines(vdf)) {
						var t = line.Trim();
						if (!t.StartsWith("\"path\"")) continue;
						var parts = t.Split('"');
						if (parts.Length >= 4) libs.Add(parts[3].Replace("\\\\", "\\"));
					}
				} catch {}
			}
			return libs.Distinct();
		}

		static bool IsGameRunning(GameBase game, string gamePath) {
			var exeName = Path.GetFileName(game.GetExecutablePath(gamePath));
			var self = Environment.ProcessId.ToString();
			try {
				foreach (var dir in Directory.GetDirectories("/proc")) {
					var pid = Path.GetFileName(dir);
					if (!int.TryParse(pid, out _) || pid == self) continue;
					try {
						// Under Proton the game's own process has the .exe as one of its arguments
						// ("Z:\...\Spider-Man2.exe" or "/.../Spider-Man2.exe"); match whole arguments only,
						// so a shell command that merely mentions the name doesn't count.
						var argv = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
						foreach (var arg in argv) {
							var baseName = arg[(arg.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
							if (baseName.Equals(exeName, StringComparison.OrdinalIgnoreCase)) return true;
						}
					} catch {}
				}
			} catch {}
			return false;
		}

		static void WarnIfGameRunning(GameBase game, string gamePath) {
			if (IsGameRunning(game, gamePath)) {
				Console.WriteLine("Warning: the game seems to be running. Installing will most likely fail unless you close it.\n");
			}
		}

		#endregion
		#region misc

		static void RegisterNativeLibraries() {
			// GDeflate.cs imports "libdeflate.dll" (its Windows name). Map it to the Linux build shipped next to us,
			// which must be NVIDIA's libdeflate fork with GDeflate support (the distro's libdeflate lacks it).
			NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, asm, path) => {
				if (name != "libdeflate.dll") return IntPtr.Zero;
				var candidates = new[] {
					Path.Combine(AppContext.BaseDirectory, "libdeflate_gdeflate.so"),
					Path.Combine(AppContext.BaseDirectory, "lib", "libdeflate_gdeflate.so"),
				};
				foreach (var c in candidates) {
					if (File.Exists(c) && NativeLibrary.TryLoad(c, out var handle)) return handle;
				}
				return IntPtr.Zero;
			});
		}

		static void RunThread(System.Threading.Thread t) {
			t.Start();
			t.Join();
		}

		static string DefaultHome() {
			var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
			if (string.IsNullOrEmpty(xdg)) xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
			return Path.Combine(xdg, "overstrike-cli");
		}

		static string ExpandHome(string p) =>
			p == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
			: p.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..])
			: p;

		static bool SamePath(string a, string b) {
			try { return RealPath(a).TrimEnd('/') == RealPath(b).TrimEnd('/'); } catch { return a == b; }
		}

		static bool Confirm(string question) {
			Console.Write($"{question} [y/N]: ");
			var a = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
			return a is "y" or "yes";
		}

		static string TakeOption(List<string> args, params string[] names) {
			for (var i = 0; i < args.Count; ++i) {
				foreach (var n in names) {
					if (args[i] == n && i + 1 < args.Count) { var v = args[i + 1]; args.RemoveRange(i, 2); return v; }
					if (args[i].StartsWith(n + "=")) { var v = args[i][(n.Length + 1)..]; args.RemoveAt(i); return v; }
				}
			}
			return null;
		}

		static bool TakeFlag(List<string> args, params string[] names) {
			var found = false;
			for (var i = args.Count - 1; i >= 0; --i) {
				if (names.Contains(args[i])) { args.RemoveAt(i); found = true; }
			}
			return found;
		}

		static int Fail(string message) {
			Console.Error.WriteLine($"Error: {message}");
			return 1;
		}

		class CliError: Exception {
			public CliError(string message): base(message) {}
		}

		#endregion
	}
}
