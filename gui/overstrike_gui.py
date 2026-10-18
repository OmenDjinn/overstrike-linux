#!/usr/bin/env python3
# Overstrike for Linux -- a simple GUI on top of overstrike-cli.
# Licensed under GPLv3, same as Overstrike. Unofficial port; Overstrike is by Tkachov.
#
# The GUI never touches game files itself: every action is an overstrike-cli command
# (the JSON "api" subcommand for reading/saving state, normal commands for install etc.).

import json
import os
import shutil
import subprocess
import sys

from PySide6.QtCore import QObject, QProcess, QProcessEnvironment, Qt, QTimer, QUrl, Signal
from PySide6.QtGui import QAction, QColor, QIcon, QPalette
from PySide6.QtWidgets import (
    QAbstractItemView, QApplication, QCheckBox, QComboBox, QDialog, QDialogButtonBox, QFileDialog,
    QFormLayout, QFrame, QGroupBox, QHBoxLayout, QHeaderView, QLabel, QLineEdit, QListWidget,
    QListWidgetItem, QMainWindow, QMessageBox, QPlainTextEdit, QProgressBar, QPushButton,
    QSizePolicy, QSplitter, QTreeWidget, QTreeWidgetItem, QVBoxLayout, QWidget,
)

APP_NAME = "Overstrike for Linux"
VERSION = "0.2.0"
MOD_EXTENSIONS = "*.smpcmod *.mmpcmod *.suit *.suit_style *.stage *.modular *.script *.zip *.7z *.rar"
PATH_ROLE = Qt.UserRole
INFO_ROLE = Qt.UserRole + 1


def resource_dir():
    if getattr(sys, "frozen", False):
        return getattr(sys, "_MEIPASS", os.path.dirname(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


def clean_env():
    """Environment for child processes without PyInstaller's/AppImage's library paths."""
    env = dict(os.environ)
    for var in ("LD_LIBRARY_PATH", "QT_PLUGIN_PATH", "QML2_IMPORT_PATH", "PYTHONHOME", "PYTHONPATH"):
        orig = env.pop(var + "_ORIG", None)
        if orig is not None:
            env[var] = orig
        elif var in env and getattr(sys, "frozen", False):
            del env[var]
    return env


def find_cli():
    candidates = [os.environ.get("OVERSTRIKE_CLI")]
    if os.environ.get("APPDIR"):
        candidates.append(os.path.join(os.environ["APPDIR"], "usr", "lib", "overstrike-cli", "overstrike-cli"))
    here = os.path.dirname(os.path.abspath(sys.executable if getattr(sys, "frozen", False) else __file__))
    candidates += [
        os.path.join(here, "overstrike-cli"),
        os.path.join(here, "..", "lib", "overstrike-cli", "overstrike-cli"),
        os.path.join(here, "..", "dist", "overstrike-cli", "overstrike-cli"),
        shutil.which("overstrike-cli"),
    ]
    for c in candidates:
        if c and os.path.isfile(c) and os.access(c, os.X_OK):
            return os.path.abspath(c)
    return None


def open_path(path):
    try:
        subprocess.Popen(["xdg-open", path], env=clean_env(), start_new_session=True,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError:
        pass


class Cli(QObject):
    """Runs overstrike-cli commands one at a time, without blocking the window."""
    output = Signal(str)
    busy_changed = Signal(bool)

    def __init__(self, path):
        super().__init__()
        self.path = path
        self.queue = []
        self.proc = None
        env = QProcessEnvironment()
        for k, v in clean_env().items():
            env.insert(k, v)
        self.env = env

    def run(self, args, done=None, stdin=None, stream=False):
        self.queue.append((args, done, stdin, stream))
        if self.proc is None:
            self._next()

    def run_sync(self, args, stdin=None, timeout=120):
        """For quick api calls where the answer is needed right away."""
        r = subprocess.run([self.path] + args, input=stdin, capture_output=True, text=True,
                           env=clean_env(), timeout=timeout)
        return r.returncode, r.stdout, r.stderr

    def _next(self):
        if not self.queue:
            self.proc = None
            self.busy_changed.emit(False)
            return
        args, done, stdin, stream = self.queue.pop(0)
        self.busy_changed.emit(True)
        proc = QProcess(self)
        proc.setProcessEnvironment(self.env)
        proc.setProgram(self.path)
        proc.setArguments(args)
        out_chunks, err_chunks = [], []

        def read_out():
            text = bytes(proc.readAllStandardOutput()).decode(errors="replace")
            out_chunks.append(text)
            if stream:
                self.output.emit(text)

        def read_err():
            text = bytes(proc.readAllStandardError()).decode(errors="replace")
            err_chunks.append(text)
            if stream:
                self.output.emit(text)

        def finished(code, _status):
            read_out()
            read_err()
            self.proc = None
            if done:
                done(code, "".join(out_chunks), "".join(err_chunks))
            self._next()

        proc.readyReadStandardOutput.connect(read_out)
        proc.readyReadStandardError.connect(read_err)
        proc.finished.connect(finished)
        proc.errorOccurred.connect(lambda e: err_chunks.append(f"Failed to start overstrike-cli: {e}\n"))
        self.proc = proc
        proc.start()
        if stdin is not None:
            proc.write(stdin.encode())
            proc.closeWriteChannel()


class ModList(QTreeWidget):
    files_dropped = Signal(list)
    order_changed = Signal()

    def __init__(self):
        super().__init__()
        self.setHeaderLabels(["Mod", "Type"])
        self.setRootIsDecorated(False)
        self.setAlternatingRowColors(True)
        self.setUniformRowHeights(True)
        self.setSelectionMode(QAbstractItemView.ExtendedSelection)
        self.setDragDropMode(QAbstractItemView.InternalMove)
        self.setDefaultDropAction(Qt.MoveAction)
        self.setAcceptDrops(True)
        self.header().setStretchLastSection(False)
        self.header().setSectionResizeMode(0, QHeaderView.Stretch)
        self.header().setSectionResizeMode(1, QHeaderView.ResizeToContents)
        self.invisibleRootItem().setFlags(self.invisibleRootItem().flags() | Qt.ItemIsDropEnabled)

    def dragEnterEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()
        else:
            super().dragEnterEvent(e)

    def dragMoveEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()
        else:
            super().dragMoveEvent(e)

    def dropEvent(self, e):
        if e.mimeData().hasUrls():
            files = [u.toLocalFile() for u in e.mimeData().urls() if u.isLocalFile()]
            e.acceptProposedAction()
            if files:
                self.files_dropped.emit(files)
            return
        super().dropEvent(e)
        self.order_changed.emit()


class GameDialog(QDialog):
    """Pick a detected game or browse to a game folder, and name the profile."""

    def __init__(self, parent, games, existing_names):
        super().__init__(parent)
        self.setWindowTitle("Add game")
        self.setMinimumWidth(560)
        self.existing = {n.lower() for n in existing_names}
        lay = QVBoxLayout(self)
        lay.addWidget(QLabel("Games found in your Steam libraries:"))
        self.games = QListWidget()
        for g in games:
            it = QListWidgetItem(f"{g['name']}\n{g['path']}")
            it.setData(PATH_ROLE, g)
            self.games.addItem(it)
        if not games:
            it = QListWidgetItem("No supported games found. Use \"Browse...\" to pick the game folder.")
            it.setFlags(Qt.NoItemFlags)
            self.games.addItem(it)
        self.games.currentItemChanged.connect(self._picked)
        lay.addWidget(self.games)

        row = QHBoxLayout()
        self.path = QLineEdit()
        self.path.setPlaceholderText("Game folder (contains 'toc' and the game .exe)")
        browse = QPushButton("Browse...")
        browse.clicked.connect(self._browse)
        row.addWidget(self.path)
        row.addWidget(browse)
        form = QFormLayout()
        form.addRow("Folder:", row)
        self.name = QLineEdit()
        form.addRow("Profile name:", self.name)
        lay.addLayout(form)

        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        buttons.accepted.connect(self._accept)
        buttons.rejected.connect(self.reject)
        lay.addWidget(buttons)
        if games:
            self.games.setCurrentRow(0)

    def _unique(self, base):
        name, i = base, 2
        while name.lower() in self.existing:
            name, i = f"{base} {i}", i + 1
        return name

    def _picked(self, item):
        g = item.data(PATH_ROLE) if item else None
        if g:
            self.path.setText(g["path"])
            self.name.setText(self._unique(g["name"].replace(":", "")))

    def _browse(self):
        d = QFileDialog.getExistingDirectory(self, "Game folder", self.path.text() or os.path.expanduser("~"))
        if d:
            self.path.setText(d)
            if not self.name.text():
                self.name.setText(self._unique(os.path.basename(d)))

    def _accept(self):
        if not self.path.text().strip() or not self.name.text().strip():
            QMessageBox.warning(self, "Add game", "Pick a game folder and a profile name.")
            return
        if self.name.text().strip().lower() in self.existing:
            QMessageBox.warning(self, "Add game", "A profile with this name already exists.")
            return
        self.accept()


class OptionsDialog(QDialog):
    def __init__(self, parent, info):
        super().__init__(parent)
        self.setWindowTitle(f"Options: {info['name']}")
        self.setMinimumWidth(420)
        lay = QVBoxLayout(self)
        if info.get("description"):
            d = QLabel(info["description"])
            d.setWordWrap(True)
            lay.addWidget(d)
        form = QFormLayout()
        self.combos = []
        for m in info["modules"]:
            c = QComboBox()
            c.addItems(m["options"])
            c.setCurrentIndex(m["selected"])
            form.addRow(m["name"] + ":", c)
            self.combos.append(c)
        lay.addLayout(form)
        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        lay.addWidget(buttons)

    def selections(self):
        return [f"{i}={c.currentIndex()}" for i, c in enumerate(self.combos)]


class MainWindow(QMainWindow):
    def __init__(self, cli):
        super().__init__()
        self.cli = cli
        self.state = None
        self.updating = False
        self.setWindowTitle(APP_NAME)
        self.resize(900, 640)
        icon = os.path.join(resource_dir(), "overstrike-linux.png")
        if os.path.exists(icon):
            self.setWindowIcon(QIcon(icon))

        self.apply_timer = QTimer(self)
        self.apply_timer.setSingleShot(True)
        self.apply_timer.setInterval(250)
        self.apply_timer.timeout.connect(self.apply_mods_state)

        self._build_menu()
        self._build_ui()
        cli.output.connect(self.append_log)
        cli.busy_changed.connect(self._busy)

        QTimer.singleShot(0, self.first_load)

    # ---------- layout ----------

    def _build_menu(self):
        m = self.menuBar().addMenu("&File")
        self._action(m, "Add mods...", self.add_mods_dialog, "Ctrl+O")
        self._action(m, "Open Mods Library folder", lambda: open_path(self.state["library"]) if self.state else None)
        m.addSeparator()
        self._action(m, "Quit", self.close, "Ctrl+Q")

        m = self.menuBar().addMenu("&Game")
        self._action(m, "Add game / profile...", self.add_game)
        self._action(m, "Delete this profile...", self.delete_profile)
        self._action(m, "Open game folder", lambda: self.current and open_path(self.current["path"]))
        m.addSeparator()
        self._action(m, "Steam launch options...", self.show_launch_options)
        self._action(m, "Reset 'toc' (after a broken update)...", self.reset_toc)

        m = self.menuBar().addMenu("&Help")
        self._action(m, "Open error log", self.open_error_log)
        self._action(m, "Open data folder", lambda: self.state and open_path(self.state["home"]))
        self._action(m, "About", self.about)

    def _action(self, menu, text, slot, shortcut=None):
        a = QAction(text, self)
        if shortcut:
            a.setShortcut(shortcut)
        a.triggered.connect(slot)
        menu.addAction(a)
        return a

    def _build_ui(self):
        central = QWidget()
        root = QVBoxLayout(central)
        root.setContentsMargins(12, 10, 12, 10)

        # profile bar
        bar = QHBoxLayout()
        bar.addWidget(QLabel("Profile:"))
        self.profile_combo = QComboBox()
        self.profile_combo.setMinimumWidth(260)
        self.profile_combo.activated.connect(self.switch_profile)
        bar.addWidget(self.profile_combo)
        add_game = QPushButton("Add game...")
        add_game.clicked.connect(self.add_game)
        bar.addWidget(add_game)
        bar.addStretch(1)
        self.status_chip = QLabel()
        self.status_chip.setObjectName("chip")
        bar.addWidget(self.status_chip)
        root.addLayout(bar)
        self.path_label = QLabel()
        self.path_label.setObjectName("dim")
        self.path_label.setTextInteractionFlags(Qt.TextSelectableByMouse)
        root.addWidget(self.path_label)

        # mod list + side panel
        split = QSplitter(Qt.Horizontal)
        left = QWidget()
        ll = QVBoxLayout(left)
        ll.setContentsMargins(0, 6, 0, 0)
        tools = QHBoxLayout()
        self.btn_add = QPushButton("Add mods...")
        self.btn_add.clicked.connect(self.add_mods_dialog)
        self.btn_remove = QPushButton("Remove")
        self.btn_remove.clicked.connect(self.remove_mods)
        self.btn_options = QPushButton("Options...")
        self.btn_options.clicked.connect(self.edit_options)
        self.filter = QLineEdit()
        self.filter.setPlaceholderText("Filter mods")
        self.filter.setClearButtonEnabled(True)
        self.filter.textChanged.connect(self.apply_filter)
        for w in (self.btn_add, self.btn_remove, self.btn_options):
            tools.addWidget(w)
        tools.addStretch(1)
        tools.addWidget(self.filter)
        ll.addLayout(tools)

        self.mods = ModList()
        self.mods.itemChanged.connect(self.item_changed)
        self.mods.itemSelectionChanged.connect(self.update_buttons)
        self.mods.itemDoubleClicked.connect(lambda it, col: self.edit_options() if it.data(0, INFO_ROLE).get("modular") else None)
        self.mods.files_dropped.connect(self.add_files)
        self.mods.order_changed.connect(self.order_changed)
        ll.addWidget(self.mods)
        hint = QLabel("Tick mods to install them. Drag to change load order: mods lower in the list win. "
                      "You can also drop mod files here.")
        hint.setObjectName("dim")
        hint.setWordWrap(True)
        ll.addWidget(hint)
        split.addWidget(left)

        side = QWidget()
        sl = QVBoxLayout(side)
        sl.setContentsMargins(10, 6, 0, 0)
        g = QGroupBox("Suits")
        gl = QFormLayout(g)
        self.lang = QComboBox()
        self.lang.activated.connect(self.settings_changed)
        gl.addRow("Name language:", self.lang)
        sl.addWidget(g)
        g = QGroupBox("Script mods")
        gl = QVBoxLayout(g)
        self.scripts = QCheckBox("Enable .script mods")
        self.scripts.toggled.connect(self.settings_changed)
        self.commandline = QCheckBox("Add -scripts to commandline.txt")
        self.commandline.toggled.connect(self.settings_changed)
        self.scripts_note = QLabel()
        self.scripts_note.setWordWrap(True)
        self.scripts_note.setObjectName("dim")
        self.scripts_note.setOpenExternalLinks(False)
        self.scripts_note.linkActivated.connect(lambda _: self.state and open_path(self.state["home"]))
        gl.addWidget(self.scripts)
        gl.addWidget(self.commandline)
        gl.addWidget(self.scripts_note)
        self.scripts_box = g
        sl.addWidget(g)
        sl.addStretch(1)
        split.addWidget(side)
        split.setStretchFactor(0, 3)
        split.setStretchFactor(1, 1)
        split.setSizes([640, 260])

        vsplit = QSplitter(Qt.Vertical)
        vsplit.addWidget(split)
        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setPlaceholderText("Install output appears here.")
        self.log.setMaximumBlockCount(5000)
        vsplit.addWidget(self.log)
        vsplit.setStretchFactor(0, 4)
        vsplit.setStretchFactor(1, 1)
        vsplit.setSizes([470, 90])
        root.addWidget(vsplit, 1)

        # bottom bar
        bottom = QHBoxLayout()
        self.progress = QProgressBar()
        self.progress.setRange(0, 0)
        self.progress.setMaximumWidth(160)
        self.progress.setTextVisible(False)
        self.progress.hide()
        self.status = QLabel()
        self.status.setObjectName("dim")
        bottom.addWidget(self.progress)
        bottom.addWidget(self.status, 1)
        self.btn_uninstall = QPushButton("Uninstall all")
        self.btn_uninstall.clicked.connect(self.uninstall)
        self.btn_install = QPushButton("Install mods")
        self.btn_install.setObjectName("primary")
        self.btn_install.setDefault(True)
        self.btn_install.clicked.connect(self.install)
        bottom.addWidget(self.btn_uninstall)
        bottom.addWidget(self.btn_install)
        root.addLayout(bottom)
        self.setCentralWidget(central)

    # ---------- state ----------

    @property
    def current(self):
        return self.state["current"] if self.state else None

    def first_load(self):
        self.refresh()
        if self.state is not None and not self.state["profiles"]:
            code, out, err = self.cli.run_sync(["setup"])
            self.append_log(out + err)
            self.refresh()
            if not self.state["profiles"]:
                QMessageBox.information(self, APP_NAME,
                                        "No supported game was found in your Steam libraries.\n"
                                        "Use \"Add game...\" to pick the game folder.")

    def refresh(self):
        code, out, err = self.cli.run_sync(["api", "state"])
        if code != 0:
            self.state = None
            self.status.setText("Couldn't read state: " + (err.strip() or out.strip()))
            return
        self.state = json.loads(out)
        self.populate()

    def populate(self):
        self.updating = True
        try:
            self.profile_combo.clear()
            cur = self.current
            for p in self.state["profiles"]:
                self.profile_combo.addItem(f"{p['name']}  ({p['gameName']})", p["name"])
            if cur:
                self.profile_combo.setCurrentIndex(self.profile_combo.findData(cur["name"]))

            self.mods.clear()
            if not cur:
                self.path_label.setText("Add a game to get started.")
                self.status_chip.setText("")
                self.set_enabled(False)
                return
            self.set_enabled(True)
            self.path_label.setText(cur["path"])
            if not cur["gameFound"]:
                self._chip("Game files not found", "#c0392b")
            elif cur["modsInstalled"]:
                self._chip("Modded", "#2e86de")
            else:
                self._chip("Vanilla", "#7f8c8d")

            for m in cur["mods"]:
                it = QTreeWidgetItem([m["name"], m["type"]])
                it.setData(0, PATH_ROLE, m["path"])
                it.setData(0, INFO_ROLE, m)
                flags = Qt.ItemIsEnabled | Qt.ItemIsSelectable | Qt.ItemIsUserCheckable
                if not m["suitsMenu"]:
                    flags |= Qt.ItemIsDragEnabled
                it.setFlags(flags)
                it.setCheckState(0, Qt.Checked if m["install"] else Qt.Unchecked)
                if m["suitsMenu"]:
                    it.setToolTip(0, "Applies suit menu changes and lists added suits in the game. Always installed last.")
                if m["modular"]:
                    it.setToolTip(0, "Modular mod: double-click to pick its options.")
                self.mods.addTopLevelItem(it)

            self.lang.clear()
            for l in cur["languages"]:
                self.lang.addItem(l["name"], l["code"])
            i = self.lang.findData(cur["language"])
            self.lang.setCurrentIndex(max(i, 0))
            self.lang.setEnabled(len(cur["languages"]) > 1)

            self.scripts_box.setVisible(cur["hasScripts"])
            self.scripts.setChecked(cur["scripts"])
            self.commandline.setChecked(cur["commandline"])
            self.commandline.setEnabled(cur["scripts"])
            if cur["scripts"] and not self.state["scriptsProxyPresent"]:
                self.scripts_note.setText(
                    "Needs <b>scripts_proxy.dll</b> from the Windows Overstrike download, copied into the "
                    "<a href='#'>data folder</a>.")
            elif cur["scripts"]:
                self.scripts_note.setText("Steam launch options: <code>WINEDLLOVERRIDES=\"winmm=n,b\" %command%</code>"
                                          + ("" if cur["commandline"] else " <code>-scripts</code>"))
            else:
                self.scripts_note.setText("")

            n = sum(1 for m in cur["mods"] if not m["suitsMenu"])
            msg = f"{n} mod(s) in library"
            if cur["skipped"]:
                msg += f" ({cur['skipped']} for other games hidden)"
            self.status.setText(msg)
            for w in cur["warnings"]:
                self.append_log("Warning: " + w + "\n")
            self.apply_filter()
            self.update_buttons()
        finally:
            self.updating = False

    def _chip(self, text, color):
        self.status_chip.setText(text)
        self.status_chip.setStyleSheet(
            f"QLabel#chip {{ background: {color}; color: white; border-radius: 9px; padding: 2px 10px; }}")

    def set_enabled(self, on):
        for w in (self.mods, self.btn_add, self.btn_remove, self.btn_options, self.btn_install,
                  self.btn_uninstall, self.lang, self.scripts, self.commandline, self.filter):
            w.setEnabled(on)

    def update_buttons(self):
        sel = [it.data(0, INFO_ROLE) for it in self.mods.selectedItems()]
        self.btn_remove.setEnabled(bool(sel) and not any(m["suitsMenu"] for m in sel))
        self.btn_options.setEnabled(len(sel) == 1 and sel[0]["modular"])

    def apply_filter(self):
        text = self.filter.text().lower()
        for i in range(self.mods.topLevelItemCount()):
            it = self.mods.topLevelItem(i)
            it.setHidden(bool(text) and text not in it.text(0).lower() and text not in it.text(1).lower())
        # reordering a filtered list would be confusing
        self.mods.setDragDropMode(QAbstractItemView.InternalMove if not text else QAbstractItemView.DropOnly)

    # ---------- edits ----------

    def item_changed(self, item, column):
        if not self.updating:
            self.apply_timer.start()

    def order_changed(self):
        # keep the Suits Menu entry last, like Overstrike does
        for i in range(self.mods.topLevelItemCount()):
            it = self.mods.topLevelItem(i)
            if it.data(0, INFO_ROLE)["suitsMenu"] and i != self.mods.topLevelItemCount() - 1:
                self.mods.takeTopLevelItem(i)
                self.mods.addTopLevelItem(it)
                break
        self.apply_timer.start()

    def mods_payload(self):
        mods = []
        for i in range(self.mods.topLevelItemCount()):
            it = self.mods.topLevelItem(i)
            mods.append({"path": it.data(0, PATH_ROLE), "install": it.checkState(0) == Qt.Checked})
        return json.dumps({"mods": mods})

    def apply_mods_state(self, then=None):
        def done(code, out, err):
            if code != 0:
                self.append_log(err or out)
            if then:
                then()
        self.cli.run(self.profile_args() + ["api", "apply"], done, stdin=self.mods_payload())

    def profile_args(self):
        return ["--profile", self.current["name"]] if self.current else []

    def settings_changed(self, *_):
        if self.updating or not self.current:
            return
        payload = {"scripts": self.scripts.isChecked(),
                   "commandline": self.commandline.isChecked() and self.scripts.isChecked(),
                   "language": self.lang.currentData() or ""}
        self.commandline.setEnabled(self.scripts.isChecked())
        self.cli.run(self.profile_args() + ["api", "settings"], lambda c, o, e: self.refresh(), stdin=json.dumps(payload))

    def switch_profile(self, index):
        name = self.profile_combo.itemData(index)
        if not name or (self.current and name == self.current["name"]):
            return
        self.flush_then(lambda: self.cli.run(["profile", "use", name], lambda c, o, e: self.refresh()))

    def flush_then(self, fn):
        """Save pending list edits before doing something else."""
        if self.apply_timer.isActive():
            self.apply_timer.stop()
            self.apply_mods_state(then=fn)
        else:
            fn()

    # ---------- actions ----------

    def add_mods_dialog(self):
        files, _ = QFileDialog.getOpenFileNames(self, "Add mods", os.path.expanduser("~/Downloads"),
                                                f"Mods ({MOD_EXTENSIONS});;All files (*)")
        if files:
            self.add_files(files)

    def add_files(self, files):
        self.append_log(f"Adding {len(files)} file(s)...\n")
        self.flush_then(lambda: self.cli.run(["add"] + files, lambda c, o, e: (self.append_log(o + e), self.refresh()),
                                             stream=False))

    def remove_mods(self):
        sel = [it.data(0, INFO_ROLE) for it in self.mods.selectedItems()]
        if not sel:
            return
        names = "\n".join("  " + m["name"] for m in sel[:15]) + ("\n  ..." if len(sel) > 15 else "")
        nested = [m for m in sel if "||" in m["path"]]
        extra = ""
        if nested:
            archives = sorted({m["path"].split("||")[0] for m in nested})
            extra = "\n\nSome of these are inside an archive; the whole archive will be deleted:\n" + \
                    "\n".join("  " + a for a in archives)
        if QMessageBox.question(self, "Remove mods",
                                f"Delete these from the Mods Library?\n\n{names}{extra}\n\n"
                                "Run \"Install mods\" afterwards to take them out of the game.") != QMessageBox.Yes:
            return
        self.flush_then(lambda: self.cli.run(["api", "remove"] + [m["path"] for m in sel],
                                             lambda c, o, e: (e and self.append_log(e), self.refresh())))

    def edit_options(self):
        items = self.mods.selectedItems()
        if len(items) != 1 or not items[0].data(0, INFO_ROLE)["modular"]:
            return
        path = items[0].data(0, PATH_ROLE)

        def show():
            code, out, err = self.cli.run_sync(self.profile_args() + ["api", "options", path])
            if code != 0:
                QMessageBox.warning(self, "Options", err or out)
                return
            info = json.loads(out)
            if not info["modules"]:
                QMessageBox.information(self, "Options", "This mod has no options to pick.")
                return
            d = OptionsDialog(self, info)
            if d.exec() == QDialog.Accepted:
                code, out, err = self.cli.run_sync(self.profile_args() + ["api", "set-options", path] + d.selections())
                if code != 0:
                    QMessageBox.warning(self, "Options", err or out)
                else:
                    self.status.setText("Options saved. Install mods to apply.")
        self.flush_then(show)

    def add_game(self):
        code, out, err = self.cli.run_sync(["api", "games"])
        games = json.loads(out) if code == 0 else []
        known = {(p["path"].rstrip("/")) for p in (self.state or {}).get("profiles", [])}
        games = [g for g in games if g["path"].rstrip("/") not in known] or games
        d = GameDialog(self, games, [p["name"] for p in (self.state or {}).get("profiles", [])])
        if d.exec() != QDialog.Accepted:
            return
        code, out, err = self.cli.run_sync(["profile", "create", d.name.text().strip(), d.path.text().strip()])
        if code != 0:
            QMessageBox.warning(self, "Add game", (err or out).replace("Error: ", ""))
        self.refresh()

    def delete_profile(self):
        if not self.current:
            return
        name = self.current["name"]
        if QMessageBox.question(self, "Delete profile",
                                f"Delete profile '{name}'?\n\nMods stay in the Mods Library, and mods already "
                                "installed in the game stay until you install or uninstall again.") == QMessageBox.Yes:
            code, out, err = self.cli.run_sync(["profile", "delete", name])
            if code != 0:
                QMessageBox.warning(self, "Delete profile", err or out)
            others = [p["name"] for p in self.state["profiles"] if p["name"] != name]
            if others:
                self.cli.run_sync(["profile", "use", others[0]])
            self.refresh()

    def install(self):
        if not self.current:
            return

        def go():
            code, out, err = self.cli.run_sync(self.profile_args() + ["api", "toc-status"])
            flag = []
            if code == 0:
                st = json.loads(out)
                if st.get("updated"):
                    def v(key):
                        return f" ({st[key.replace('Date', 'Version')]})" if st.get(key.replace('Date', 'Version')) else ""
                    box = QMessageBox(self)
                    box.setWindowTitle("Game was updated")
                    box.setIcon(QMessageBox.Question)
                    box.setText("The game's 'toc' changed since mods were last installed. Most likely the game was updated.")
                    box.setInformativeText(
                        f"Current toc: {st['tocDate']}{v('tocDate')}\nBackup toc: {st['bakDate']}{v('bakDate')}\n\n"
                        "Use the new 'toc' as the clean backup? This is the right choice after a game update.")
                    yes = box.addButton("Use new toc", QMessageBox.YesRole)
                    keep = box.addButton("Keep old backup", QMessageBox.NoRole)
                    box.addButton(QMessageBox.Cancel)
                    box.setDefaultButton(yes)
                    box.exec()
                    if box.clickedButton() == yes:
                        flag = ["--update-backup"]
                    elif box.clickedButton() == keep:
                        flag = ["--keep-backup"]
                    else:
                        return
            if self.scripts.isChecked() and not self.state["scriptsProxyPresent"]:
                QMessageBox.warning(self, "Script mods",
                                    "Script mods are enabled, but scripts_proxy.dll is missing.\n\n"
                                    "Copy it from the Windows Overstrike download into the data folder "
                                    "(Help > Open data folder), or turn script mods off.")
                return
            self.log.clear()
            self.cli.run(self.profile_args() + ["install"] + flag, self.install_done, stream=True)
        self.flush_then(go)

    def install_done(self, code, out, err):
        self.refresh()
        if code == 0:
            self.status.setText("Mods installed. Start the game from Steam.")
        else:
            QMessageBox.warning(self, "Install failed",
                                "Installing mods failed. See the log at the bottom for details.\n\n"
                                "If the game was updated recently, use Game > Reset 'toc', verify the game "
                                "files in Steam, then install again.")

    def uninstall(self):
        if not self.current:
            return
        if QMessageBox.question(self, "Uninstall all",
                                "Remove all mods from the game and restore its original files?\n"
                                "Your Mods Library is kept.") != QMessageBox.Yes:
            return
        self.log.clear()
        self.cli.run(self.profile_args() + ["uninstall"],
                     lambda c, o, e: (self.refresh(), self.status.setText("Game restored to vanilla." if c == 0 else "Uninstall failed, see log.")),
                     stream=True)

    def reset_toc(self):
        if not self.current:
            return
        if QMessageBox.question(self, "Reset 'toc'",
                                "This deletes 'toc', 'toc.BAK' and the game .exe, so Steam re-downloads clean copies.\n\n"
                                "Afterwards, in Steam: right-click the game > Properties > Installed Files > "
                                "Verify integrity of game files. Then install mods again.\n\nContinue?") != QMessageBox.Yes:
            return
        self.log.clear()
        self.cli.run(self.profile_args() + ["-y", "reset-toc"], lambda c, o, e: self.refresh(), stream=True)

    def show_launch_options(self):
        code, out, err = self.cli.run_sync(self.profile_args() + ["launch-options"])
        box = QMessageBox(self)
        box.setWindowTitle("Steam launch options")
        box.setText(out or err)
        box.setTextInteractionFlags(Qt.TextSelectableByMouse)
        box.exec()

    def open_error_log(self):
        if not self.state:
            return
        path = os.path.join(self.state["home"], "errors.log")
        if os.path.exists(path):
            open_path(path)
        else:
            QMessageBox.information(self, "Error log", "No errors have been logged.")

    def about(self):
        QMessageBox.about(self, f"About {APP_NAME}",
                          f"<b>{APP_NAME}</b> {VERSION}<br><br>"
                          "An unofficial Linux port of <a href='https://github.com/Tkachov/Overstrike'>Overstrike</a>, "
                          "the mod manager for Insomniac's PC ports, by Tkachov.<br><br>"
                          "Mods are installed by Overstrike's own code, through overstrike-cli.<br>"
                          "License: GPLv3.")

    # ---------- misc ----------

    def append_log(self, text):
        if not text:
            return
        self.log.moveCursor(self.log.textCursor().MoveOperation.End)
        self.log.insertPlainText(text)
        self.log.ensureCursorVisible()
        last = [l for l in text.strip().splitlines() if l.strip()]
        if last and self.progress.isVisible():
            self.status.setText(last[-1].strip()[:120])

    def _busy(self, busy):
        self.progress.setVisible(busy)
        self.profile_combo.setEnabled(not busy)
        for w in (self.btn_install, self.btn_uninstall):
            w.setEnabled(not busy and self.current is not None)

    def closeEvent(self, e):
        if self.apply_timer.isActive():
            self.apply_timer.stop()
            self.cli.run_sync(self.profile_args() + ["api", "apply"], stdin=self.mods_payload())
        if self.cli.proc is not None:
            if QMessageBox.question(self, APP_NAME, "An operation is still running. Quit anyway?") != QMessageBox.Yes:
                e.ignore()
                return
        e.accept()


STYLE = """
QLabel#dim { color: palette(placeholder-text); }
QPushButton { padding: 5px 14px; }
QPushButton#primary { background: #2e86de; color: white; border: none; border-radius: 4px; padding: 6px 18px; font-weight: bold; }
QPushButton#primary:hover { background: #3b95ec; }
QPushButton#primary:disabled { background: palette(mid); color: palette(window); }
QTreeWidget { padding: 2px; }
QTreeWidget::item { padding: 3px 2px; }
QPlainTextEdit { font-family: monospace; font-size: 9pt; }
"""


def apply_theme(app):
    app.setStyle("Fusion")
    dark = False
    try:
        dark = app.styleHints().colorScheme() == Qt.ColorScheme.Dark
    except Exception:
        pass
    if os.environ.get("OVERSTRIKE_THEME") in ("dark", "light"):
        dark = os.environ["OVERSTRIKE_THEME"] == "dark"
    if dark:
        p = QPalette()
        base, window, text = QColor("#1e1f22"), QColor("#2b2d31"), QColor("#e6e6e6")
        p.setColor(QPalette.Window, window)
        p.setColor(QPalette.WindowText, text)
        p.setColor(QPalette.Base, base)
        p.setColor(QPalette.AlternateBase, QColor("#25262a"))
        p.setColor(QPalette.Text, text)
        p.setColor(QPalette.Button, QColor("#383a40"))
        p.setColor(QPalette.ButtonText, text)
        p.setColor(QPalette.Highlight, QColor("#2e86de"))
        p.setColor(QPalette.HighlightedText, QColor("white"))
        p.setColor(QPalette.ToolTipBase, window)
        p.setColor(QPalette.ToolTipText, text)
        p.setColor(QPalette.PlaceholderText, QColor("#8e9297"))
        p.setColor(QPalette.Link, QColor("#5dade2"))
        p.setColor(QPalette.Disabled, QPalette.Text, QColor("#6d6f75"))
        p.setColor(QPalette.Disabled, QPalette.ButtonText, QColor("#6d6f75"))
        p.setColor(QPalette.Disabled, QPalette.WindowText, QColor("#6d6f75"))
        p.setColor(QPalette.Light, QColor("#4a4d55"))
        p.setColor(QPalette.Midlight, QColor("#3d4047"))
        p.setColor(QPalette.Mid, QColor("#2f3136"))
        p.setColor(QPalette.Dark, QColor("#191a1d"))
        app.setPalette(p)
        # Fusion's checkbox outline is nearly invisible on a dark base; draw our own
        check = os.path.join(resource_dir(), "check.svg").replace("\\", "/")
        indicator = (
            "QTreeView::indicator, QCheckBox::indicator { width: 14px; height: 14px; border: 1px solid #8e9297;"
            " border-radius: 3px; background: #1e1f22; }"
            f"QTreeView::indicator:checked, QCheckBox::indicator:checked {{ background: #2e86de; border-color: #2e86de; image: url({check}); }}"
            "QCheckBox::indicator:disabled { border-color: #4a4d55; background: #2b2d31; }"
        )
        app.setStyleSheet(STYLE + indicator)
        return
    app.setStyleSheet(STYLE)


def main():
    app = QApplication(sys.argv)
    app.setApplicationName(APP_NAME)
    app.setDesktopFileName("overstrike-linux")
    apply_theme(app)
    cli_path = find_cli()
    if not cli_path:
        QMessageBox.critical(None, APP_NAME, "overstrike-cli was not found.\n"
                                             "Set OVERSTRIKE_CLI to its path, or put it next to this program.")
        return 1
    w = MainWindow(Cli(cli_path))
    w.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
