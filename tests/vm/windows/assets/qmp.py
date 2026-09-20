#!/usr/bin/env python3
"""検証 VM（UTM / QEMU）へ QMP でキー入力・マウス・画面取り込みを送る。create-vm.sh が `-qmp tcp:127.0.0.1:4445,server,nowait` を足している。
  qmp.py keys ctrl-alt-delete            # QEMU の qcode 名を - で繋ぐ（同時押し）。複数はスペース区切りで順に
  qmp.py chord ctrl-shift-r              # 修飾キー→本体キーの順に押して逆順に離す（ショートカットはこちら）
  qmp.py release                         # 押しっぱなしになったキーを全部離す
  qmp.py type 'Hello, world'             # ASCII を 1 文字ずつ（shift を付ける）
  qmp.py shot out.png                    # UTM の VM ウインドウを screencapture で PNG に（screendump は sandbox で書けない）
  qmp.py click X Y [left|right]          # 絶対座標（画面のピクセル）でクリック
  qmp.py raw '{"execute":"query-status"}'
macOS の Accessibility 許可が要らず、ゲストのどの状態（UEFI・Setup・ログオン画面）でも使える。"""
import json, socket, sys, time, subprocess, os

HOST, PORT = "127.0.0.1", int(os.environ.get("QMP_PORT", "4445"))

class Qmp:
    def __init__(self):
        self.s = socket.create_connection((HOST, PORT), timeout=10)
        self.f = self.s.makefile("rb")
        self._recv()  # greeting
        self.cmd("qmp_capabilities")
    def _recv(self):
        while True:
            line = self.f.readline()
            if not line: raise RuntimeError("qmp closed")
            m = json.loads(line)
            if "event" in m: continue
            return m
    def cmd(self, name, **args):
        self.s.sendall((json.dumps({"execute": name, "arguments": args}) + "\n").encode())
        r = self._recv()
        if "error" in r: raise RuntimeError(r["error"])
        return r.get("return")

SHIFTED = {'!':'1','@':'2','#':'3','$':'4','%':'5','^':'6','&':'7','*':'8','(':'9',')':'0','_':'minus','+':'equal','{':'bracket_left','}':'bracket_right','|':'backslash',':':'semicolon','"':'apostrophe','<':'comma','>':'dot','?':'slash','~':'grave_accent'}
PLAIN = {' ':'spc','-':'minus','=':'equal','[':'bracket_left',']':'bracket_right','\\':'backslash',';':'semicolon',"'":'apostrophe',',':'comma','.':'dot','/':'slash','`':'grave_accent','\n':'ret','\t':'tab'}

def keys(q, combo, hold=60):
    q.cmd("send-key", keys=[{"type": "qcode", "data": k} for k in combo.split("-")], **{"hold-time": hold})

def key_event(q, qcode, down):
    q.cmd("input-send-event", events=[{"type": "key", "data": {"down": down, "key": {"type": "qcode", "data": qcode}}}])

def chord(q, combo):
    """修飾キーを順に押してから本体キーを押し、逆順で離す（send-key の同時押しより実機のショートカットに近い）"""
    ks = combo.split("-")
    for k in ks: key_event(q, k, True); time.sleep(0.05)
    time.sleep(0.08)
    for k in reversed(ks): key_event(q, k, False); time.sleep(0.05)

def release_all(q):
    for k in ["ctrl", "ctrl_r", "shift", "shift_r", "alt", "alt_r", "meta_l", "meta_r"] + [chr(c) for c in range(ord("a"), ord("z") + 1)] + [str(d) for d in range(10)]:
        key_event(q, k, False)

def type_text(q, text):
    # ゲストの入力処理が追いつかないと文字が落ちたり順が入れ替わるので、1 文字ずつ押して離し、間隔を空ける
    delay = float(os.environ.get("QMP_TYPE_DELAY", "0.09"))
    for ch in text:
        if ch in SHIFTED: chord(q, "shift-" + SHIFTED[ch])
        elif ch in PLAIN: chord(q, PLAIN[ch])
        elif ch.isalpha(): chord(q, ("shift-" if ch.isupper() else "") + ch.lower())
        elif ch.isdigit(): chord(q, ch)
        else: raise SystemExit(f"type: unsupported char {ch!r}")
        time.sleep(delay)

def shot(q, out):
    # QEMU（UTM の sandbox 内の helper）は screendump でファイルを書けないので、UTM の VM ウインドウを screencapture で撮る
    wid = subprocess.run([os.path.expanduser("~/.cache/utm/winid"), os.environ.get("VM_NAME", "ReplyFive Windows")], capture_output=True, text=True).stdout.strip()
    subprocess.run(["screencapture", "-x", "-o", "-l", wid, out], check=True)

def click(q, x, y, button="left"):
    q.cmd("input-send-event", events=[{"type": "abs", "data": {"axis": "x", "value": int(x * 32767 / q.width)}}, {"type": "abs", "data": {"axis": "y", "value": int(y * 32767 / q.height)}}])
    time.sleep(0.1)
    q.cmd("input-send-event", events=[{"type": "btn", "data": {"down": True, "button": button}}])
    time.sleep(0.08)
    q.cmd("input-send-event", events=[{"type": "btn", "data": {"down": False, "button": button}}])

if __name__ == "__main__":
    a = sys.argv[1:]
    q = Qmp()
    if a[0] == "keys":
        for k in a[1:]: keys(q, k); time.sleep(0.15)
    elif a[0] == "chord":
        for k in a[1:]: chord(q, k); time.sleep(0.2)
    elif a[0] == "release": release_all(q)
    elif a[0] == "type": type_text(q, a[1])
    elif a[0] == "shot": shot(q, a[1]); print(a[1])
    elif a[0] == "click":
        # 画面サイズ：QMP_SCREEN=WxH があればそれ、無ければ VM ウインドウの screencapture（表題バー 40px を引く）から
        size = os.environ.get("QMP_SCREEN")
        if size: q.width, q.height = map(int, size.lower().split("x"))
        else:
            tmp = "/tmp/claude/qmp-size.png"; shot(q, tmp)
            out = subprocess.run(["sips", "-g", "pixelWidth", "-g", "pixelHeight", tmp], capture_output=True, text=True).stdout
            w = int(out.split("pixelWidth:")[1].split()[0]); h = int(out.split("pixelHeight:")[1].split()[0])
            q.width, q.height = w, h - 40
        click(q, int(a[1]), int(a[2]), a[3] if len(a) > 3 else "left")
    elif a[0] == "raw":
        m = json.loads(a[1]); print(json.dumps(q.cmd(m["execute"], **m.get("arguments", {}))))
    else: raise SystemExit(__doc__)
