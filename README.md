# overstrike-cli

A native Linux, command-line build of **[Overstrike](https://github.com/Tkachov/Overstrike)**, the mod manager for Insomniac's PC ports. It works with Marvel's Spider-Man 2, Spider-Man Remastered, Miles Morales, and Ratchet & Clank: Rift Apart.

It uses Overstrike's own installer code, so it installs `.stage`, `.suit`, `.suit_style`, `.modular`, `.smpcmod` and `.script` mods the same way the Windows app does. Only the Windows GUI is replaced, with a terminal interface. You don't need Wine, Proton, or the .NET Desktop Runtime.

> This is an unofficial port and is not made by Tkachov. Report problems with this port here, not to Overstrike.

## Install

### AppImage (with a window)

Download `Overstrike-Linux-0.2.0-x86_64.AppImage`, make it executable, and run it:

```sh
chmod +x Overstrike-Linux-0.2.0-x86_64.AppImage
./Overstrike-Linux-0.2.0-x86_64.AppImage
```

You can also right-click it in your file manager → Properties → allow executing, then double-click it. On the first start it finds your games in Steam. Then add mods (button, or drag files onto the list), tick the ones you want, and click **Install mods**.

The same file also runs the command-line tool: `./Overstrike-Linux-0.2.0-x86_64.AppImage cli list`.

If it doesn't start and mentions FUSE, install `fuse2`/`libfuse2` from your distro, or run it with `--appimage-extract-and-run`.

### Command line only

Download `overstrike-cli-0.2.0-linux-x64.tar.gz` and extract it:

```sh
mkdir -p ~/Applications
tar xzf overstrike-cli-0.2.0-linux-x64.tar.gz -C ~/Applications
~/Applications/overstrike-cli/overstrike-cli --version
```

The prebuilt AppImage and binary work on distros with **glibc 2.38 or newer**. That includes Arch, CachyOS, Bazzite, SteamOS 3.6+, Fedora 39+, Ubuntu 24.04+ and Mint 22+. On older distros (Debian 12, Ubuntu 22.04), use the portable build with the .NET 10 runtime, or build it from source (see below).

Optional, so you can type `overstrike-cli` from anywhere:

```sh
mkdir -p ~/.local/bin && ln -s ~/Applications/overstrike-cli/overstrike-cli ~/.local/bin/
```

## Quick start (Spider-Man 2)

1. **Back up your saves.** If you remove a suit mod while your save still has that suit selected, the game can crash.
2. Find the game and create a profile:
   ```sh
   overstrike-cli setup
   ```
   This reads your Steam libraries, including other drives, Flatpak Steam and Snap Steam.
3. Add mods. You can add mod files directly, or `.zip`/`.7z`/`.rar` archives with mods inside:
   ```sh
   overstrike-cli add ~/Downloads/cool_suit.suit ~/Downloads/some_mod.zip
   ```
4. Check the mod list and load order. New mods are enabled by default:
   ```sh
   overstrike-cli list
   ```
5. Install the mods:
   ```sh
   overstrike-cli install
   ```
6. Start the game **from Steam as usual**. Regular mods need no launch options.

## Commands

| Command | What it does |
|---|---|
| `setup` | Find installed games and create a profile for each |
| `add FILE...` | Copy mods into the Mods Library |
| `list` | Show mods in load order (`[x]` = enabled) |
| `enable N...` / `disable N...` | Toggle mods by number, by part of the name, or with `all` |
| `move N TO` | Change load order. Mods lower in the list win when two mods change the same files |
| `options N [M=O ...]` | Show or pick the options of a `.modular` mod, for example `options 3 color=blue 2=1` |
| `install` | Apply all enabled mods. Run it again after any change |
| `uninstall` | Restore the original `toc` (vanilla game) |
| `scripts on [--commandline]` / `scripts off` | Toggle `.script` mod support |
| `language CODE` | Language for suit names (default `us`) |
| `reset-toc` | Delete `toc`, `toc.BAK` and the game `.exe` so Steam re-downloads clean copies |
| `profile list/create/use/delete` | Manage profiles, for example one per game or per mod set |
| `launch-options` | Show the Steam launch options needed for scripts |
| `where` | Show where the data folder, library and logs are |

Global options: `-p NAME` picks a profile, `-y` skips prompts, `-v` prints Overstrike's detailed log, and `--home DIR` sets the data folder.

Data is stored in `~/.local/share/overstrike-cli/` (`Mods Library/`, `Profiles/`, `errors.log`).

## Script mods (`.script`)

Script mods need Overstrike's `scripts_proxy.dll`. It's a Windows DLL that loads inside the game under Proton, so it is not included here.

1. Copy `scripts_proxy.dll` from the Windows Overstrike download (it's next to `Overstrike.exe`) into `~/.local/share/overstrike-cli/`.
2. Turn scripts on and install:
   ```sh
   overstrike-cli scripts on --commandline
   overstrike-cli install
   ```
3. In Steam, open **Properties → General → Launch Options** for the game and set:
   ```
   WINEDLLOVERRIDES="winmm=n,b" %command%
   ```
   Proton ignores DLLs in the game folder unless you tell it to load them. If you didn't use `--commandline`, add `-scripts` at the end of that line.

## After a game update

Game updates replace `toc`. When you run `install`, overstrike-cli notices this and offers to use the new `toc` as the clean backup. Say **yes** (or pass `--update-backup`).

If mods still break the game after an update:

```sh
overstrike-cli reset-toc
# Steam: right-click the game → Properties → Installed Files → Verify integrity of game files
overstrike-cli install
```

## Moving over from Windows Overstrike

Copy your Windows `Mods Library` and `Profiles` folders into `~/.local/share/overstrike-cli/`. Then edit the `"path"` line in each profile `.json` so it points at the Linux game folder. Mod order, enabled states, Suits Menu changes and script settings are all kept.

## Limitations

* There is no visual Suits Menu editor (reordering or hiding suits). Suits Menu changes already saved in a profile from Windows Overstrike are applied, and newly added suits show up in the game's menu as usual.
* `.modular` options are picked by number or name with `options`, not in a visual wizard, so the option icons aren't shown.
* **Testing status:** the full pipeline was tested against a synthetic Spider-Man 2 install, including `toc` parsing and saving, GDeflate and LZ4 archive decompression, stage mods, `.modular` options, mods inside archives, uninstall and update detection. It has **not yet been tested against the real game files**. Suit and Suits Menu installs read real game data, so treat this as a beta and keep your saves backed up.

## Building from source

```sh
./build.sh                    # self-contained build → dist/overstrike-cli/
./build.sh --portable         # needs the .NET 10 runtime to run
tests/run_tests.sh            # end-to-end tests against a fake game install
appimage/build-appimage.sh    # GUI + CLI AppImage → dist/ (needs: pip install PySide6-Essentials pyinstaller)
```

The GUI (`gui/overstrike_gui.py`, Qt/PySide6) is a thin layer: every action runs `overstrike-cli`, and it reads state through `overstrike-cli api ...` (JSON). You can also run it straight from source with `python3 gui/overstrike_gui.py` after building the CLI.

You need git, make, gcc or clang, Python 3 (for the tests) and the .NET 10 SDK. `build.sh` clones Overstrike at a pinned commit, applies `patches/`, and builds NVIDIA's libdeflate fork for GDeflate support.

### What was changed from Overstrike

* **`OverstrikeCLI/Program.cs`**: the new terminal front end, which replaces the WPF window.
* **`OverstrikeCLI/Compat/`**: small stand-ins for GUI-only types (banner images, the suits-menu tab's config loader), plus a two-method `Ionic.Zlib` shim built on .NET's own zlib.
* **`patches/0001-overstrike-linux-fixes.patch`**:
  * `toc` stores archive paths Windows-style (`d\mods\mod0`). These are now resolved to real Linux paths, case-insensitively, when opening archives.
  * Stage mods and scripts zipped on Windows (with `\` separators) are now handled on Linux.
  * `GDeflate.cs` declared libdeflate's `size_t` parameters as 32-bit `uint`, and the native side wrote 8 bytes into a 4-byte variable. They are now `nuint`. Also fixed a decompressor that was never freed.
  * `ErrorLogger` got an optional console echo, used by `-v`.

## License

GPLv3, the same as Overstrike. Overstrike is © Tkachov and contributors. This port bundles NVIDIA's libdeflate fork (MIT), SharpCompress (MIT), K4os.Compression.LZ4 (MIT) and Newtonsoft.Json (MIT).
