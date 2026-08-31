"""Localhost viewer server for baseviz.

Serves the static canvas viewer plus two JSON endpoints:
  GET /api/layouts            -> [{name, path}] of discoverable layout XML files
  GET /api/layout?path=FILE   -> {ir, things, terrain} render model for one layout

The render model resolves every distinct cell token to a color/size/glyph via the
catalog, so the JS viewer just draws lookups.

Vendored from rimworld-tools/baseviz @ eabba3e by spec 2.5; the layout search
root is now resolved lazily (it used to require RIMWORLD_ROOT at import time).
See README.md in this directory. This viewer draws LAYOUTS, not map dumps — the
map-dump channel is render.py, which writes a file instead of serving a page.
"""

from __future__ import annotations

import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse, parse_qs, unquote

from . import ir as ir_mod
from .catalog import Catalog, rw_root

STATIC = Path(__file__).parent / "static"


def _layout_search_roots() -> list[Path]:
    """Where to look for layout XML to list in the picker.

    Vendored-in change (2.5): was a module-level constant built from
    ``RW_ROOT``, which made merely IMPORTING this module require the env var.
    `rwa render` imports the package for `catalog`/`png`/`render` and must not
    be held hostage by the browser viewer's needs, so the lookup is deferred to
    the point of use. See `catalog.rw_root`.
    """
    return [rw_root() / "Mods"]


def _load_catalog(scope: str = "full") -> Catalog:
    dump = Catalog.default_dump_path()
    if dump.is_file():
        print(f"[baseviz] using runtime catalog: {dump} (scope={scope})")
        return Catalog.load(dump, scope=scope)
    print(f"[baseviz] runtime dump not found; building offline catalog (scope={scope})")
    return Catalog.build(scope=scope if scope != "full" else "full")


def _discover_layouts() -> list[dict]:
    out = []
    for root in _layout_search_roots():
        if not root.is_dir():
            continue
        for f in sorted(root.rglob("StructureLayoutDefs/*.xml")):
            out.append({"name": f"{f.parent.parent.parent.name}/{f.stem}",
                        "path": str(f)})
    return out


class Handler(BaseHTTPRequestHandler):
    catalog: Catalog = None  # set in serve()

    def log_message(self, *a):  # quieter
        pass

    def _send(self, code, body: bytes, ctype):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _json(self, obj, code=200):
        self._send(code, json.dumps(obj).encode(), "application/json")

    def do_GET(self):
        u = urlparse(self.path)
        if u.path == "/api/layouts":
            return self._json(_discover_layouts())
        if u.path == "/api/layout":
            return self._serve_layout(parse_qs(u.query))
        return self._serve_static(u.path)

    def _serve_layout(self, q):
        path = unquote((q.get("path") or [""])[0])
        if not path or not Path(path).is_file():
            return self._json({"error": "missing or unknown path"}, 404)
        try:
            ir = ir_mod.parse_xml(path)
        except Exception as e:  # noqa: BLE001
            return self._json({"error": str(e)}, 400)
        things, terrain = self.catalog.build_palettes(ir)
        return self._json({"ir": ir, "things": things, "terrain": terrain})

    def _serve_static(self, path):
        rel = "index.html" if path in ("/", "") else path.lstrip("/")
        f = (STATIC / rel).resolve()
        if not str(f).startswith(str(STATIC.resolve())) or not f.is_file():
            return self._send(404, b"not found", "text/plain")
        ctype = {"html": "text/html", "js": "application/javascript",
                 "css": "text/css"}.get(f.suffix.lstrip("."), "application/octet-stream")
        self._send(200, f.read_bytes(), ctype)


def serve(host="127.0.0.1", port=8765, scope="full"):
    Handler.catalog = _load_catalog(scope)
    print(f"[baseviz] {len(Handler.catalog.defs)} defs loaded")
    httpd = ThreadingHTTPServer((host, port), Handler)
    print(f"[baseviz] viewer at http://{host}:{port}/")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        httpd.shutdown()
