"""
LeishenFanMcp 协议自检脚本（只读 + 曲线原样写回，不改变风扇实际行为）。

用法： python probe.py
"""
import json
import os
import subprocess
import sys

EXE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   "bin", "Release", "net5.0-windows", "LeishenFanMcp.exe")


def run(reqs):
    payload = "".join(json.dumps(r, ensure_ascii=False) + "\n" for r in reqs)
    p = subprocess.run([EXE], input=payload.encode("utf-8"), capture_output=True)
    if p.returncode != 0:
        print("exit=", p.returncode, p.stderr.decode("utf-8", "replace")[:400])
    lines = p.stdout.decode("utf-8", "replace").strip().splitlines()
    return [json.loads(x) for x in lines if x.strip()]


def payload(resp):
    text = resp["result"]["content"][0]["text"]
    return json.loads(text)


def main():
    reqs = [
        {"jsonrpc": "2.0", "id": 1, "method": "initialize",
         "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                    "clientInfo": {"name": "probe", "version": "0.1"}}},
        {"jsonrpc": "2.0", "method": "notifications/initialized"},
        {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
        {"jsonrpc": "2.0", "id": 3, "method": "tools/call",
         "params": {"name": "fan_list_profiles", "arguments": {}}},
    ]
    out = run(reqs)
    for r in out:
        if r.get("id") == 1:
            print("initialize:", r["result"]["serverInfo"], r["result"]["protocolVersion"])
        elif r.get("id") == 2:
            print("tools:", ", ".join(t["name"] for t in r["result"]["tools"]))
        elif r.get("id") == 3:
            prof = payload(r)
            slot1 = prof["profiles"][0]
            print("slot1 cpu:", slot1["cpu"])
            print("slot1 gpu:", slot1["gpu"])
            print("slot1 sys:", slot1["sys"])

            # 原样写回，验证写链路（内容不变，不改变实际行为）
            out2 = run([
                {"jsonrpc": "2.0", "id": 10, "method": "tools/call",
                 "params": {"name": "fan_set_curve", "arguments": {
                     "slot": 1, "cpu": slot1["cpu"], "gpu": slot1["gpu"], "sys": slot1["sys"]}}},
                {"jsonrpc": "2.0", "id": 11, "method": "tools/call",
                 "params": {"name": "fan_status", "arguments": {}}},
            ])
            for r2 in out2:
                if r2.get("id") == 10:
                    res = payload(r2)
                    print("set_curve(saved=%s) cpu=%s" % (res.get("saved"), res.get("cpu")))
                elif r2.get("id") == 11:
                    st = payload(r2)
                    print("status: ec=%s ctrl=%s slot=%s temps=%s rpm=%s" % (
                        st["ecAvailable"], st["customControlEnabled"], st["activeSlot"],
                        st["temperaturesC"], st["fansRpm"]))
                    return

    print("unexpected output", file=sys.stderr)


if __name__ == "__main__":
    main()
