"""baseviz CLI: serve the viewer, build a catalog, or convert layouts.

  python -m baseviz serve [--port 8765]
  python -m baseviz catalog [--scope vanilla|packs|full] [-o catalog.json]
  python -m baseviz parse  <layout.xml> <out.ir.json>     # KCSG XML -> IR
  python -m baseviz export <in.ir.json> <out.xml>         # IR -> KCSG XML
  python -m baseviz to-canvas   <layout.xml> <out.txt>    # XML -> hand-edit ASCII canvas
  python -m baseviz from-canvas <in.txt> <out.xml>        # edited canvas -> XML
  python -m baseviz view <layout.xml|ir.json>             # composite ASCII (read-only judge)
"""

import argparse
import sys

from . import ir as ir_mod
from .catalog import Catalog


def main(argv=None):
    argv = argv if argv is not None else sys.argv[1:]
    p = argparse.ArgumentParser(prog="baseviz")
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("serve", help="run the localhost viewer")
    s.add_argument("--port", type=int, default=8765)
    s.add_argument("--host", default="127.0.0.1")
    s.add_argument("--scope", choices=["vanilla", "full"], default="full",
                   help="vanilla = Core + DLC defs only")

    c = sub.add_parser("catalog", help="build a catalog from def XML (offline fallback)")
    c.add_argument("--scope", choices=["vanilla", "packs", "full"], default="full")
    c.add_argument("-o", "--out", default="catalog.json")

    pr = sub.add_parser("parse", help="KCSG layout XML -> IR JSON")
    pr.add_argument("xml")
    pr.add_argument("out")

    ex = sub.add_parser("export", help="IR JSON -> KCSG layout XML")
    ex.add_argument("ir")
    ex.add_argument("out")

    tc = sub.add_parser("to-canvas", help="KCSG XML -> hand-edit ASCII canvas")
    tc.add_argument("xml")
    tc.add_argument("out")

    fc = sub.add_parser("from-canvas", help="edited ASCII canvas -> KCSG XML")
    fc.add_argument("canvas")
    fc.add_argument("out")

    vw = sub.add_parser("view", help="composite ASCII view (read-only judge surface)")
    vw.add_argument("src", help="layout .xml or ir .json")

    args = p.parse_args(argv)

    if args.cmd == "serve":
        from . import server
        server.serve(args.host, args.port, args.scope)
    elif args.cmd == "catalog":
        cat = Catalog.build(scope=args.scope)
        cat.save(args.out)
        print(f"wrote {args.out}: {len(cat.defs)} defs (scope={args.scope})")
    elif args.cmd == "parse":
        ir = ir_mod.parse_xml(args.xml)
        ir_mod.save_ir(ir, args.out)
        print(f"wrote {args.out}: {ir['defName']} {ir['size']}")
    elif args.cmd == "export":
        ir = ir_mod.load_ir(args.ir)
        from pathlib import Path
        Path(args.out).write_text(ir_mod.to_xml(ir))
        print(f"wrote {args.out}")
    elif args.cmd == "to-canvas":
        from . import canvas
        from pathlib import Path
        ir = ir_mod.parse_xml(args.xml)
        Path(args.out).write_text(canvas.to_canvas(ir))
        print(f"wrote {args.out}: {ir['defName']} {ir['size']} canvas")
    elif args.cmd == "from-canvas":
        from . import canvas
        from pathlib import Path
        ir = canvas.from_canvas(Path(args.canvas).read_text())
        Path(args.out).write_text(ir_mod.to_xml(ir))
        print(f"wrote {args.out}: {ir['defName']} {ir['size']}")
    elif args.cmd == "view":
        from . import canvas
        if args.src.endswith(".json"):
            ir = ir_mod.load_ir(args.src)
        else:
            ir = ir_mod.parse_xml(args.src)
        print(canvas.composite_view(ir), end="")


if __name__ == "__main__":
    main()
