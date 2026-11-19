"""Print first-session traces from TLC 1.7.4's action-labelled state graph."""
import hashlib
from pathlib import Path
import re
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
MODEL = ROOT / "docs/formal/GameSession.tla"
CONFIG = MODEL.with_suffix(".cfg")
JAR_SHA = "936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def first_session(dot):
    # Decode state labels, never the TLC fingerprints (which only identify nodes).
    nodes = {}
    edges = {}
    initial = None
    for line in dot.splitlines():
        if match := re.match(r'^(-?\d+) -> (-?\d+) \[label="(\w+)"', line):
            source, target, action = match.groups()
            edges.setdefault(source, []).append((action, target))
        elif match := re.match(r'^(-?\d+) \[label=', line):
            node = match[1]
            values = dict((name, (first, second)) for name, first, second in re.findall(
                r'(\w+) = \(g1 :> (\w+) @@ g2 :> (\w+)\)', line.replace(r'\"', '')))
            if set(values) != {"sstate", "handle", "playtime"}:
                raise ValueError(f"unrecognized TLC state: {line}")
            nodes[node] = tuple(
                "/".join(values[key][game] for key in ("sstate", "handle", "playtime"))
                for game in range(2))
            if "style = filled" in line:
                if initial is not None:
                    raise ValueError("expected a single initial state")
                initial = node

    def walk(node, path, visited):
        # Keep the second game at Init and stop before a new tracker would be needed.
        choices = [(action, target) for action, target in edges.get(node, [])
                   if action != "Reset" and nodes[target][1] == nodes[initial][1]]
        if not choices:
            yield "\t".join(path)
        for action, target in choices:
            if target in visited:
                raise ValueError(f"cycle within one session at {path}")
            yield from walk(target, [*path, action, nodes[target][0]], visited | {target})

    return sorted(walk(initial, [nodes[initial][0]], {initial}))


def main(jar):
    if digest(jar) != JAR_SHA:
        raise ValueError("expected the pinned tla2tools 1.7.4 jar")
    with tempfile.TemporaryDirectory() as temp:
        graph = Path(temp) / "graph.dot"
        subprocess.run(["java", "-XX:+UseParallelGC", "-Xmx512m", "-cp", str(jar), "tlc2.TLC",
                        "-workers", "1", "-fp", "0", "-seed", "1", "-deadlock",
                        "-metadir", "states", "-dump", "dot,actionlabels", str(graph),
                        "-config", str(CONFIG), str(MODEL)], cwd=temp, stdout=sys.stderr,
                       check=True, timeout=60)
        traces = first_session(graph.read_text())
    print(digest(MODEL), digest(CONFIG), sep="\t")
    print(*traces, sep="\n")


if __name__ == "__main__":
    main(Path(sys.argv[1]).resolve())
