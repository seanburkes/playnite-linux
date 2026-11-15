#!/usr/bin/python3
"""AT-SPI screen-reader probe for the Avalonia synthetic window.

Requires at-spi2-core and the system python GObject bindings
(/usr/bin/python3 -c "from gi.repository import Atspi").

  probe_atspi.py --pid <app pid> [--json out.json]
  probe_atspi.py --name Substring [--json out.json] [--act]

Prints the desktop application list a screen reader would discover, then walks
the target's accessible tree (names, roles, states, interfaces, text, actions).
With --act it also drives the checkbox, the buttons and the text field through
AT-SPI exactly as a screen reader would, and re-reads the text afterwards.
Text is read through the org.a11y.atspi.Text D-Bus method because pyatspi's
Python override of get_text() is not usable.
"""
import argparse
import json
import os
import re
import subprocess
import sys

import gi

gi.require_version("Atspi", "2.0")
from gi.repository import Atspi  # noqa: E402

A11Y_BUS = os.environ.get("AT_SPI_BUS_ADDRESS") or (
    "unix:path="
    + os.environ.get("XDG_RUNTIME_DIR", f"/run/user/{os.getuid()}")
    + "/at-spi/bus_0"
)

Atspi.init()


def gdbus(bus, path, iface, method, *args):
    out = subprocess.check_output(
        ["gdbus", "call", "--address", A11Y_BUS, "--dest", bus,
         "--object-path", path, "--method", f"{iface}.{method}", *args],
        text=True, cwd="/tmp", stderr=subprocess.DEVNULL)
    return out.strip()


def find_app_bus(pid):
    names = gdbus("org.freedesktop.DBus", "/org/freedesktop/DBus",
                  "org.freedesktop.DBus", "ListNames")
    for name in re.findall(r"'(:[0-9.]+)'", names):
        if pid is not None:
            try:
                owner = gdbus("org.freedesktop.DBus", "/org/freedesktop/DBus",
                              "org.freedesktop.DBus", "GetConnectionUnixProcessID",
                              name)
            except Exception:
                continue
            if f"uint32 {pid}" in owner:
                return name
            continue
        try:
            info = gdbus(name, "/org/a11y/atspi/accessible/root",
                         "org.freedesktop.DBus.Properties", "Get",
                         "org.a11y.atspi.Accessible", "Name")
        except Exception:
            continue
        if "Avalonia Application" in info:
            return name
    return None


def interfaces(obj):
    try:
        return sorted(obj.get_interfaces() or [])
    except Exception as exc:
        return [f"<error:{exc}>"]


def states(obj):
    out = []
    try:
        ss = obj.get_state_set()
    except Exception:
        return out
    for st in ("ACTIVE", "CHECKED", "EDITABLE", "ENABLED", "EXPANDED", "FOCUSABLE",
               "FOCUSED", "PRESSED", "SELECTED", "SENSITIVE", "SHOWING", "VISIBLE"):
        enum = getattr(Atspi.StateType, st, None)
        if enum is not None and ss.contains(enum):
            out.append(st.lower())
    return out


def actions(obj):
    out = []
    a = obj.get_action_iface()
    if a is not None:
        for i in range(a.get_n_actions()):
            try:
                out.append({"index": i, "name": a.get_action_name(i)})
            except Exception:
                out.append({"index": i, "name": "<error>"})
    return out


def read_text(obj, bus):
    if "Text" not in interfaces(obj):
        return None
    try:
        count = obj.get_text_iface().get_character_count()
        value = gdbus(bus, obj.path, "org.a11y.atspi.Text", "GetText", "0", str(count))
        match = re.match(r"\('(.*)'\,\)$", value, re.S)
        return match.group(1) if match else value
    except Exception as exc:
        return f"<Text interface present; read failed: {exc}>"


def walk(obj, bus, depth=0, limit=400, out=None):
    if out is None:
        out = []
    node = {
        "depth": depth,
        "name": None,
        "role": None,
        "description": None,
        "states": [],
        "interfaces": [],
        "text": None,
        "value": None,
        "actions": [],
        "path": "",
        "children": [],
    }
    out.append(node)
    if len(out) > limit:
        return node
    try:
        node["name"] = obj.get_name()
        node["role"] = obj.get_role_name()
        node["description"] = obj.get_description()
        node["path"] = obj.path
    except Exception:
        pass
    node["states"] = states(obj)
    node["interfaces"] = interfaces(obj)
    node["text"] = read_text(obj, bus)
    v = obj.get_value_iface()
    if v is not None:
        try:
            node["value"] = v.get_current_value()
        except Exception:
            pass
    node["actions"] = actions(obj)
    try:
        count = obj.get_child_count()
    except Exception:
        count = 0
    for i in range(count):
        if len(out) > limit:
            break
        try:
            child = obj.get_child_at_index(i)
        except Exception:
            continue
        if child is not None:
            node["children"].append(walk(child, bus, depth + 1, limit, out))
    return node


def find_by_role(node, role, name):
    if node["role"] == role and node["name"] == name:
        return node
    for child in node["children"]:
        hit = find_by_role(child, role, name)
        if hit is not None:
            return hit
    return None


def do_action(obj, index=0):
    a = obj.get_action_iface()
    if a is None:
        return {"acted": False, "why": "no Action interface"}
    try:
        ok = a.do_action(index)
        return {"acted": bool(ok), "action": a.get_action_name(index)}
    except Exception as exc:
        return {"acted": False, "why": str(exc)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pid", type=int)
    ap.add_argument("--name")
    ap.add_argument("--json")
    ap.add_argument("--act", action="store_true")
    args = ap.parse_args()

    desktop = Atspi.get_desktop(0)
    apps = []
    target = None
    for i in range(desktop.get_child_count()):
        app = desktop.get_child_at_index(i)
        if app is None:
            continue
        try:
            pid = app.get_process_id()
        except Exception:
            pid = None
        apps.append({"name": app.get_name(), "pid": pid, "role": app.get_role_name()})
        if target is not None:
            continue
        if args.pid is not None and pid == args.pid:
            target = app
        elif args.name is not None and args.name.lower() in (app.get_name() or "").lower():
            target = app

    result = {"desktop_apps": apps, "target": None, "tree": None}
    if target is None:
        print("DESKTOP_APPS " + json.dumps(apps))
        print("TARGET not found")
        if args.json:
            with open(args.json, "w", encoding="utf-8") as fh:
                json.dump(result, fh, indent=2, ensure_ascii=False)
        return 2

    bus = find_app_bus(args.pid)
    print(f"TARGET name={target.get_name()!r} pid={target.get_process_id()} bus={bus}")
    tree = walk(target, bus)
    result["target"] = target.get_name()
    result["bus"] = bus
    result["tree"] = tree
    print("TREE " + json.dumps(tree, indent=2, ensure_ascii=False))

    if args.act:
        acted = {}
        interactive = {}

        def collect(node):
            if node["actions"] or node["role"] == "entry":
                interactive[(node["role"], node["name"])] = node
            for child in node["children"]:
                collect(child)

        collect(tree)

        def obj_for(role, name):
            for node in walk_nodes(target):
                try:
                    if node.get_role_name() == role and node.get_name() == name:
                        return node
                except Exception:
                    pass
            return None

        def walk_nodes(root):
            yield root
            for i in range(root.get_child_count()):
                child = root.get_child_at_index(i)
                if child is not None:
                    yield from walk_nodes(child)

        for role, name in [("check box", "Installed only"),
                           ("button", "Play Orbit"),
                           ("button", "Details unavailable")]:
            obj = obj_for(role, name)
            acted[f"{role}:{name}"] = do_action(obj) if obj is not None else {"acted": False, "why": "not found"}

        entry = obj_for("entry", "Search games")
        if entry is None:
            acted["set_text"] = {"ok": False, "why": "not found"}
        else:
            et = entry.get_editable_text_iface()
            if et is None:
                acted["set_text"] = {"ok": False, "why": "no EditableText interface"}
            else:
                try:
                    acted["set_text"] = {"ok": bool(et.set_text_contents(0, 5, "Meadow"))}
                except Exception:
                    try:
                        acted["set_text"] = {"ok": bool(et.set_text_contents("Meadow"))}
                    except Exception as exc2:
                        acted["set_text"] = {"ok": False, "why": str(exc2)}

        if args.json:
            result["actions"] = acted
        print("ACTIONS " + json.dumps(acted, ensure_ascii=False))

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(result, fh, indent=2, ensure_ascii=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
