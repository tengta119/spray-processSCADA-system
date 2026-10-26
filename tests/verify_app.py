"""Windows app smoke check using a disposable copy and a loopback Modbus server.

Run after building the Debug app: python tests/verify_app.py
Uses an ephemeral TCP port, never port 502 or the source database/configuration.
The temporary directory is retained so its isolated database and logs can be inspected.
No third-party Python packages are required.
"""

from __future__ import annotations

import argparse
import configparser
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import struct
import subprocess
import tempfile
import threading
import time


class FakeModbusServer:
    def __init__(self, port: int = 0):
        self.port = port
        self.stop_event = threading.Event()
        self.listener = None
        self.client = None
        self.thread = None
        self.requests = {1: 0, 3: 0}
        self.errors = []
        self.registers = bytearray(65536 * 2)
        for offset, value in {12: 120.0, 14: 180.0, 16: 25.0, 18: 60.0}.items():
            self.registers[offset * 2 : offset * 2 + 4] = struct.pack(">f", value)

    def start(self):
        self.stop_event.clear()
        self.listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.listener.bind(("127.0.0.1", self.port))
        self.port = self.listener.getsockname()[1]
        assert self.port > 1024 and self.port != 502
        self.listener.listen(1)
        self.listener.settimeout(0.2)
        self.thread = threading.Thread(target=self.serve, daemon=True)
        self.thread.start()

    def stop(self):
        self.stop_event.set()
        for stream in (self.client, self.listener):
            if stream:
                try:
                    stream.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                stream.close()
        if self.thread:
            self.thread.join(timeout=2)
        self.listener = self.client = None

    def receive(self, connection, count):
        result = bytearray()
        while len(result) < count:
            try:
                chunk = connection.recv(count - len(result))
            except socket.timeout:
                if self.stop_event.is_set():
                    raise EOFError
                continue
            if not chunk:
                raise EOFError
            result.extend(chunk)
        return bytes(result)

    def serve(self):
        while not self.stop_event.is_set():
            try:
                connection, _ = self.listener.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            self.client = connection
            connection.settimeout(0.2)
            try:
                with connection:
                    while not self.stop_event.is_set():
                        header = self.receive(connection, 7)
                        transaction, protocol, length, unit = struct.unpack(">HHHB", header)
                        assert protocol == 0 and unit == 1 and length == 6
                        function, offset, count = struct.unpack(">BHH", self.receive(connection, length - 1))
                        assert function in (1, 3), f"Unexpected function {function}"
                        self.requests[function] += 1
                        if function == 1:
                            data = bytes((count + 7) // 8)
                        else:
                            data = bytes(self.registers[offset * 2 : (offset + count) * 2])
                        response = bytes((function, len(data))) + data
                        connection.sendall(struct.pack(">HHHB", transaction, 0, len(response) + 1, unit) + response)
            except (EOFError, OSError):
                pass
            except Exception as error:
                self.errors.append(repr(error))
            finally:
                self.client = None


def windows_for_pid(pid):
    user32 = ctypes.windll.user32
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    windows = []

    def text_of(hwnd):
        length = user32.GetWindowTextLengthW(hwnd)
        text = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, text, length + 1)
        return text.value

    def visit(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value != pid:
            return True
        class_name = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, class_name, 256)
        windows.append({"handle": hwnd, "title": text_of(hwnd), "class": class_name.value})
        return True

    user32.EnumWindows(callback_type(visit), 0)
    return windows


def assert_app_healthy(process):
    assert process.poll() is None, f"Application exited with code {process.returncode}"
    windows = windows_for_pid(process.pid)
    dialogs = [window for window in windows if window["class"] == "#32770"]
    assert not dialogs, f"Unexpected native dialog: {dialogs}"
    return windows


def start_app(executable, foreign_cwd):
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0  # SW_HIDE: keep the smoke test out of the user's foreground.
    return subprocess.Popen(
        [str(executable)], cwd=foreign_cwd, startupinfo=startup,
        creationflags=subprocess.CREATE_NO_WINDOW,
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )


def stop_app(process):
    if process is None or process.poll() is not None:
        return
    for window in windows_for_pid(process.pid):
        ctypes.windll.user32.PostMessageW(window["handle"], 0x0010, 0, 0)  # WM_CLOSE, this PID only.
    try:
        process.wait(timeout=4)
    except subprocess.TimeoutExpired:
        process.terminate()
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=3)


def database_snapshot(path):
    with sqlite3.connect(path.as_uri() + "?mode=ro", uri=True, timeout=1) as database:
        count = database.execute('SELECT count(*) FROM "data"').fetchone()[0]
        row = database.execute(
            'SELECT "厂内温度", "厂内湿度", "水分炉测量温度", "固化炉测量温度" '
            'FROM "data" ORDER BY "Id" DESC LIMIT 1'
        ).fetchone()
        roles = database.execute('SELECT "Role" FROM "auth" ORDER BY "Id"').fetchall()
        users = database.execute('SELECT count(*) FROM "user"').fetchone()[0]
    return {"count": count, "values": row, "roles": [role[0] for role in roles], "users": users}


def wait_for_snapshot(database, process, condition, seconds=25):
    deadline = time.monotonic() + seconds
    last = None
    while time.monotonic() < deadline:
        assert_app_healthy(process)
        if database.exists():
            try:
                last = database_snapshot(database)
                if condition(last):
                    return last
            except sqlite3.OperationalError:
                pass
        time.sleep(0.25)
    raise AssertionError(f"Database condition not met within {seconds}s; last={last}")


def run(build_dir):
    assert os.name == "nt", "This smoke check requires Windows and the Windows Desktop runtime."
    executable_name = "Yale.SprayProcessSCADASystem.exe"
    assert (build_dir / executable_name).is_file(), f"Build the app first: {build_dir}"
    workspace = Path(tempfile.mkdtemp(prefix="yale-scada-smoke-")).resolve()
    app_dir = workspace / "app"
    foreign_cwd = workspace / "foreign-cwd"
    foreign_cwd.mkdir()

    def ignore_test_data(_directory, names):
        return [name for name in names if name == "Logs" or name.endswith((".db", ".db-shm", ".db-wal"))]

    shutil.copytree(build_dir, app_dir, ignore=ignore_test_data)
    database = app_dir / "SprayData.db"
    assert not database.exists(), "The test must start without a copied database."
    server = FakeModbusServer()
    process = None
    result = {"temporary_directory": str(workspace)}
    print(json.dumps(result), flush=True)
    try:
        server.start()
        config = configparser.ConfigParser()
        config.optionxform = str
        config.read(app_dir / "config.ini", encoding="utf-8-sig")
        config["PLC参数"].update({
            "通信协议": "ModbusTcp", "PLC地址": "127.0.0.1", "PLC端口": str(server.port),
            "站号": "1", "浮点字序": "ABCD", "变量表地址": "PLC_Modbus_Config.xlsx",
            "超时时间": "500", "循环读取时间": "200", "重连时间": "500",
        })
        config["系统参数"].update({"删除文件夹路径": "Logs", "保存文件夹天数": "不清理"})
        with (app_dir / "config.ini").open("w", encoding="utf-8") as output:
            config.write(output, space_around_delimiters=False)

        process = start_app(app_dir / executable_name, foreign_cwd)
        first = wait_for_snapshot(database, process, lambda snapshot: snapshot["count"] >= 2)
        assert tuple(map(float, first["values"])) == (25.0, 60.0, 120.0, 180.0), first
        assert set(first["roles"]) == {"工程师", "操作员", "访客"} and len(first["roles"]) == 3, first
        assert first["users"] == 0, "Startup must not create a default login."
        assert all(server.requests.values()), f"Expected coil/register reads: {server.requests}"
        assert not (foreign_cwd / "SprayData.db").exists(), "Database leaked into the caller's cwd."
        result["connected"] = first

        server.stop()
        # Allow the prior in-flight sample to settle, then verify the disconnected app remains alive.
        settle_until = time.monotonic() + 2
        while time.monotonic() < settle_until:
            assert_app_healthy(process)
            time.sleep(0.25)
        disconnected = database_snapshot(database)["count"]
        server.start()
        reconnected = wait_for_snapshot(database, process, lambda snapshot: snapshot["count"] >= disconnected + 2)
        assert tuple(map(float, reconnected["values"])) == (25.0, 60.0, 120.0, 180.0), reconnected
        result["reconnected"] = {"rows_before": disconnected, "rows_after": reconnected["count"]}
        result["windows"] = [window["title"] for window in assert_app_healthy(process) if window["title"]]
        stop_app(process)
        process = None
        server.stop()

        before_offline = database_snapshot(database)["count"]
        process = start_app(app_dir / executable_name, foreign_cwd)
        offline_until = time.monotonic() + 6
        while time.monotonic() < offline_until:
            assert_app_healthy(process)
            time.sleep(0.25)
        offline_windows = [window["title"] for window in assert_app_healthy(process) if window["title"]]
        assert offline_windows, "Offline application has no top-level window."
        after_offline = database_snapshot(database)["count"]
        assert after_offline == before_offline, "Offline app unexpectedly recorded samples."
        result["offline"] = {"alive_seconds": 6, "rows_unchanged": after_offline, "windows": offline_windows}
        assert not server.errors, server.errors
        result["modbus_requests"] = server.requests
        result["status"] = "passed"
        print(json.dumps(result, ensure_ascii=False, indent=2), flush=True)
    except Exception:
        result["status"] = "failed"
        if process and process.poll() is None:
            result["windows"] = windows_for_pid(process.pid)
        print(json.dumps(result, ensure_ascii=False, indent=2), flush=True)
        raise
    finally:
        stop_app(process)
        server.stop()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-dir", type=Path,
                        default=Path(__file__).resolve().parents[1] / "Yale.SprayProcessSCADASystem" /
                        "bin" / "Debug" / "net10.0-windows")
    run(parser.parse_args().build_dir.resolve())
