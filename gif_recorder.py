# -*- coding: utf-8 -*-
"""
轻量 GIF 区域录制小工具（Windows）

操作流程：
    1. 按 Ctrl+Shift+R（可在下方配置区修改），鼠标拖拽框选要录制的区域
    2. 松开鼠标后立即开始录制，屏幕右上角出现红色录制指示器
    3. 再按一次快捷键 / 按 Esc / 点击指示器的"停止"按钮 → 停止并导出 GIF
    4. GIF 保存在「图片 / GIF 录制」文件夹，并自动打开该文件夹

安装运行：
    pip install -r requirements.txt
    python gif_recorder.py

打包成 exe（可选）：
    pip install pyinstaller
    pyinstaller --noconsole --onefile --name GIF录制 gif_recorder.py
"""

import glob
import os
import shutil
import sys
import tempfile
import threading
import time
from datetime import datetime

# ============================ 配置区（按需修改） ============================
HOTKEY_TOGGLE = "ctrl+shift+r"  # 开始 / 停止 的全局快捷键
FPS = 10                        # 录制帧率：越大越流畅，文件也越大
MAX_SECONDS = 60                # 单次最长录制秒数（防止文件过大）
OUTPUT_DIR = os.path.join(os.path.expanduser("~"), "Pictures", "GIF 录制")
# ===========================================================================


def check_deps():
    """检查第三方依赖，返回缺失的 pip 包名列表。"""
    missing = []
    for mod, pip_name in (("keyboard", "keyboard"),
                          ("mss", "mss"),
                          ("PIL", "pillow")):
        try:
            __import__(mod)
        except ImportError:
            missing.append(pip_name)
    return missing


def enable_dpi_awareness():
    """让 tkinter 坐标与 mss 截图像素坐标一致（仅 Windows 有效）。"""
    if sys.platform != "win32":
        return
    import ctypes
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # Per-Monitor V2
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


def select_region():
    """全屏半透明遮罩，拖拽框选区域。

    返回 (left, top, width, height)，按 Esc 取消则返回 None。
    """
    import tkinter as tk

    result = {"bbox": None}

    root = tk.Tk()
    root.attributes("-fullscreen", True)
    root.attributes("-topmost", True)
    root.attributes("-alpha", 0.25)
    root.overrideredirect(True)
    root.configure(bg="black", cursor="crosshair")

    canvas = tk.Canvas(root, bg="black", highlightthickness=0)
    canvas.pack(fill="both", expand=True)

    hint = tk.Label(
        root,
        text="拖拽鼠标框选录制区域，松开即开始录制（Esc 取消）",
        fg="white", bg="black", font=("Microsoft YaHei", 14),
    )
    hint.place(relx=0.5, rely=0.06, anchor="center")

    state = {"x0": 0, "y0": 0, "rect": None}

    def on_press(e):
        state["x0"], state["y0"] = e.x, e.y

    def on_drag(e):
        if state["rect"]:
            canvas.delete(state["rect"])
        state["rect"] = canvas.create_rectangle(
            state["x0"], state["y0"], e.x, e.y, outline="red", width=2
        )

    def on_release(e):
        x1, y1 = state["x0"], state["y0"]
        x2, y2 = e.x, e.y
        left, top = min(x1, x2), min(y1, y2)
        width, height = abs(x2 - x1), abs(y2 - y1)
        if width > 10 and height > 10:
            result["bbox"] = (left, top, width, height)
        root.destroy()

    def on_esc(_e):
        root.destroy()

    canvas.bind("<ButtonPress-1>", on_press)
    canvas.bind("<B1-Motion>", on_drag)
    canvas.bind("<ButtonRelease-1>", on_release)
    root.bind("<Escape>", on_esc)

    root.mainloop()
    return result["bbox"]


def record_region(bbox, stop_event, tmpdir):
    """按 FPS 截取 bbox 区域，存为临时 PNG 序列。返回文件路径列表。"""
    import mss as mss_lib
    from PIL import Image

    left, top, width, height = bbox
    monitor = {"left": left, "top": top, "width": width, "height": height}
    paths = []
    interval = 1.0 / FPS
    deadline = time.time() + MAX_SECONDS

    with mss_lib.mss() as sct:
        i = 0
        while not stop_event.is_set() and time.time() < deadline:
            t0 = time.time()
            shot = sct.grab(monitor)
            img = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
            path = os.path.join(tmpdir, "frame_%05d.png" % i)
            img.save(path)
            paths.append(path)
            i += 1
            spent = time.time() - t0
            if spent < interval:
                time.sleep(interval - spent)
    return paths


def show_indicator(stop_event):
    """右上角录制指示器：红点 + 计时 + 停止按钮。stop_event 置位后自动关闭。"""
    import tkinter as tk

    win = tk.Tk()
    win.overrideredirect(True)
    win.attributes("-topmost", True)
    win.attributes("-alpha", 0.92)
    win.configure(bg="#222222")

    w, h = 230, 66
    x = win.winfo_screenwidth() - w - 24
    win.geometry("%dx%d+%d+24" % (w, h, x))

    start = time.time()
    var = tk.StringVar(value="● 录制中 00:00")

    def tick():
        if stop_event.is_set():
            win.destroy()
            return
        secs = int(time.time() - start)
        var.set("● 录制中 %02d:%02d" % (secs // 60, secs % 60))
        win.after(200, tick)

    tk.Label(win, textvariable=var, fg="#ff4444", bg="#222222",
             font=("Microsoft YaHei", 13, "bold")).pack(pady=(8, 0))
    tk.Button(win, text="停止并导出 GIF",
              command=stop_event.set).pack(pady=4)

    win.bind("<Escape>", lambda _e: stop_event.set())
    win.after(200, tick)
    win.mainloop()


def export_gif(frame_paths, out_path, fps):
    """把 PNG 序列合成为 GIF。

    从抽样帧构建全局调色板：体积更小、颜色不闪烁，
    也不会像"只用第一帧调色板"那样洗掉后面的颜色。
    """
    from PIL import Image

    frames = [Image.open(p).convert("RGB") for p in frame_paths]
    try:
        # 均匀抽样最多 8 帧，缩略后拼成一张图，求全局自适应调色板
        step = max(1, len(frames) // 8)
        thumbs = []
        for f in frames[::step][:8]:
            t = f.copy()
            t.thumbnail((64, 64))
            thumbs.append(t)
        combo_w = sum(t.width for t in thumbs)
        combo_h = max(t.height for t in thumbs)
        combo = Image.new("RGB", (combo_w, combo_h))
        x = 0
        for t in thumbs:
            combo.paste(t, (x, 0))
            x += t.width
        palette_base = combo.convert("P", palette=Image.ADAPTIVE, colors=256)
        qframes = [f.quantize(palette=palette_base) for f in frames]
    except Exception:
        # 兜底：逐帧自适应调色板
        qframes = [f.convert("P", palette=Image.ADAPTIVE, colors=256)
                   for f in frames]
    qframes[0].save(
        out_path, save_all=True, append_images=qframes[1:],
        duration=int(1000 / fps), loop=0, optimize=True,
    )
    for f in frames:
        f.close()
    return out_path


def run_session(ctx):
    """一次完整的「框选 → 录制 → 导出」流程（在工作线程中运行）。"""
    bbox = select_region()
    if not bbox:
        print("已取消框选")
        return

    print("开始录制区域 %s，再按 %s / Esc / 点停止按钮结束"
          % (bbox, HOTKEY_TOGGLE))
    stop_event = threading.Event()
    ctx["stop_event"] = stop_event
    tmpdir = tempfile.mkdtemp(prefix="gifrec_")
    try:
        holder = {}
        rec = threading.Thread(
            target=lambda: holder.update(
                frames=record_region(bbox, stop_event, tmpdir)),
            daemon=True,
        )
        rec.start()
        show_indicator(stop_event)  # 阻塞，直到停止
        rec.join()

        frames = sorted(glob.glob(os.path.join(tmpdir, "frame_*.png")))
        if not frames:
            print("没有录到任何画面")
            return
        os.makedirs(OUTPUT_DIR, exist_ok=True)
        name = datetime.now().strftime("gif_%Y%m%d_%H%M%S.gif")
        out = os.path.join(OUTPUT_DIR, name)
        print("正在导出 GIF（共 %d 帧）..." % len(frames))
        export_gif(frames, out, FPS)
        size_kb = os.path.getsize(out) // 1024
        print("已保存：%s（约 %d KB）" % (out, size_kb))
        try:
            os.startfile(OUTPUT_DIR)  # 自动打开输出文件夹（仅 Windows）
        except Exception:
            pass
    finally:
        ctx["stop_event"] = None
        shutil.rmtree(tmpdir, ignore_errors=True)


def main():
    missing = check_deps()
    if missing:
        sys.exit("缺少依赖，请先运行：pip install " + " ".join(missing))
    if sys.platform != "win32":
        print("提示：此工具为 Windows 设计，其他系统仅部分功能可用。")
    enable_dpi_awareness()
    os.makedirs(OUTPUT_DIR, exist_ok=True)

    import keyboard

    ctx = {"busy": False, "stop_event": None}
    lock = threading.Lock()

    def on_hotkey():
        with lock:
            if ctx["busy"]:
                ev = ctx["stop_event"]
                if ev is not None:
                    ev.set()  # 录制中 → 停止
                return        # 框选中 → 忽略本次按键
            ctx["busy"] = True

        def _run():
            try:
                run_session(ctx)
            finally:
                with lock:
                    ctx["busy"] = False
                    ctx["stop_event"] = None

        threading.Thread(target=_run, daemon=True).start()

    keyboard.add_hotkey(HOTKEY_TOGGLE, on_hotkey)
    print("GIF 录制小工具已启动，按 %s 框选并开始录制（关闭此窗口即退出）"
          % HOTKEY_TOGGLE)
    keyboard.wait()  # 永久阻塞，保持监听


if __name__ == "__main__":
    main()
