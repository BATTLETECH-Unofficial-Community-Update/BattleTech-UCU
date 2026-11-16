"""Writes tools/release-gate/src/gog-accept.tsv from a content-equivalence TSV (path, kind, verdict, detail, steam_sha256,
gog_sha256): every file judged equivalent, except the kinds named with --exclude-kind, with both installs' SHA-512.

    python -I gog-accept.py <content-equivalence.tsv> <out.tsv> [--steam <dir>] [--gog <dir>] [--exclude-kind mdd ...]

Each row's SHA-256s are re-checked against the two installs first, so a TSV from other builds is refused. The committed
list came from .hermes/reports/logic-gog-support-1-20261115/content-equivalence.tsv with --exclude-kind mdd (the MDD's
rows are equivalent but in another order, which SQLite reads may expose).
"""
import argparse, hashlib, os, sys

ap = argparse.ArgumentParser()
ap.add_argument("tsv"); ap.add_argument("out")
ap.add_argument("--steam", default=r"D:\Steam\steamapps\common\BATTLETECH")
ap.add_argument("--gog", default=r"F:\Games\BATTLETECH")
ap.add_argument("--exclude-kind", action="append", default=[])
a = ap.parse_args()

def hashes(p):
    s2, s5 = hashlib.sha256(), hashlib.sha512()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            s2.update(b); s5.update(b)
    return s2.hexdigest(), s5.hexdigest()

rows, skipped = [], {}
with open(a.tsv, encoding="utf-8") as f:
    head = f.readline().rstrip("\n").split("\t")
    for line in f:
        r = dict(zip(head, line.rstrip("\n").split("\t")))
        if r["verdict"] != "equivalent" or r["kind"] in a.exclude_kind:
            skipped[r["kind"] + ":" + r["verdict"]] = skipped.get(r["kind"] + ":" + r["verdict"], 0) + 1
            continue
        s2, s5 = hashes(os.path.join(a.steam, r["path"]))
        g2, g5 = hashes(os.path.join(a.gog, r["path"]))
        if s2 != r["steam_sha256"].lower() or g2 != r["gog_sha256"].lower():
            sys.exit("install bytes don't match the TSV: " + r["path"])
        rows.append((r["path"], s5, g5, r["kind"] + ": " + r["detail"].replace("\t", " ")[:160]))

with open(a.out, "w", encoding="utf-8", newline="\n") as o:
    o.write("# Retail files whose GOG 1.9.1 bytes differ from Steam 1.9.1's but are content-equivalent for the player, so a GOG install\n"
            "# copies GOG's own file (ReleasePack --gog). A file not listed here is installed on GOG from Steam's bytes, packed.\n"
            "# Columns: retail path, Steam SHA-512, GOG SHA-512, basis. SHA-512 only: this file is public source (PublicManifest.cs).\n"
            "# Evidence: .hermes/reports/logic-gog-support-1-20261115/content-equivalence.md; written by tools/release-gate/gog-accept.py.\n"
            "# Left to Steam's bytes: "
            + ", ".join(f"{k} {v}" for k, v in sorted(skipped.items())) + ".\n")
    for r in sorted(rows):
        o.write("\t".join(r) + "\n")
print(len(rows), "accepted;", "skipped", skipped)
