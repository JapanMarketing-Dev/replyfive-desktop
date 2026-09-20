#!/usr/bin/env python3
"""結合テスト用の会話アプリ（GTK3）。Slack 風に「送信者 → 本文」の並びで会話を出し、下に返信欄（Entry）を置いて焦点を当てる。
返信欄の内容が変わるたびに /tmp/entry.txt へ書く（差し込みの検証用）。アプリ名は "Slack" にして platform 判定を通す。"""
import gi, sys
gi.require_version("Gtk", "3.0")
from gi.repository import Gtk, GLib
GLib.set_prgname("slack")  # AT-SPI のアプリ名（実際の Slack Linux 版と同じく "slack"）

CONVERSATION = [
    ("山田", "リリース日の件、どうなりそうですか？"),
    ("遠藤", "10/22なら間に合います。"),
    ("山田", "了解です。資料は前日までに共有してください。"),
]

class Chat(Gtk.Window):
    def __init__(self):
        super().__init__(title="#general - JapanMarketing - Slack")
        self.set_default_size(720, 560)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        self.add(box)
        box.pack_start(Gtk.Label(label="#general"), False, False, 4)
        scroller = Gtk.ScrolledWindow()
        scroller.set_vexpand(True)
        messages = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        for sender, text in CONVERSATION:
            s = Gtk.Label(label=sender, xalign=0)
            t = Gtk.Label(label=text, xalign=0)
            messages.pack_start(s, False, False, 0)
            messages.pack_start(t, False, False, 0)
        scroller.add(messages)
        box.pack_start(scroller, True, True, 0)
        self.entry = Gtk.Entry()
        self.entry.set_placeholder_text("Message #general")
        self.entry.connect("changed", self.on_changed)
        box.pack_start(self.entry, False, False, 6)
        self.connect("destroy", Gtk.main_quit)

    def on_changed(self, entry):
        with open("/tmp/entry.txt", "w", encoding="utf-8") as f:
            f.write(entry.get_text())

win = Chat()
win.show_all()
win.entry.grab_focus()
GLib.idle_add(lambda: win.present() or False)
Gtk.main()
