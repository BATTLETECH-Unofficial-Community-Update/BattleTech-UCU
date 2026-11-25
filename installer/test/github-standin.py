"""Local stand-in for the parts of GitHub's REST API that ReleasePublish.exe uses (src/PublishCli.cs, GitHubReleases.cs).
Never talks to GitHub. Point the CLI at it with BTUCU_GITHUB_API=http://127.0.0.1:<port>/repos/.

  GET    /repos/{o}/{r}                     full_name, permissions.push
  GET    /repos/{o}/{r}/commits             [] (not empty)
  GET    /repos/{o}/{r}/releases?page=N     drafts included, page 1 only
  POST   /repos/{o}/{r}/releases            create
  PATCH  /repos/{o}/{r}/releases/{id}       draft / body (publishing sets published_at)
  DELETE /repos/{o}/{r}/releases/assets/{id}
  GET    /repos/{o}/{r}/attestations/sha256:{hex}   one attestation for every hash
  POST   /upload/{o}/{r}/{id}?name=N        an asset: size, state uploaded, digest sha256
  POST   /_corrupt?repo=o/r&tag=T&name=N    test hook: change that asset's digest
Usage: py -3.12 -I github-standin.py --port 18766"""
import argparse, datetime, hashlib, json, re, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

ap = argparse.ArgumentParser()
ap.add_argument("--port", type=int, default=18766)
ap.add_argument("--counts", action="store_true", help="give assets a download_count (3 for the Windows installer and package.vault, 9 for the macOS kit and package-mac.vault)")
args = ap.parse_args()
base = f"http://127.0.0.1:{args.port}"
lock = threading.Lock()
repos = {}  # "o/r" -> list of releases
ids = [100]


def stamp():
    # published_at: one second apart, in the order releases are published.
    return (datetime.datetime(2026, 1, 1) + datetime.timedelta(seconds=nid())).strftime("%Y-%m-%dT%H:%M:%SZ")


def nid():
    ids[0] += 1
    return ids[0]


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def send(self, code, obj=None):
        b = b"" if obj is None else json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    def route(self, method):
        u = urlparse(self.path)
        q = parse_qs(u.query)
        if not self.headers.get("Authorization", "").startswith("Bearer "):
            return self.send(401, {"message": "no token"})
        if method == "POST" and u.path == "/_corrupt":
            with lock:
                for r in repos.get(q["repo"][0], []):
                    if r["tag_name"] == q["tag"][0]:
                        for a in r["assets"]:
                            if a["name"] == q["name"][0]:
                                a["digest"] = "sha256:" + "0" * 64
            return self.send(200, {})
        m = re.fullmatch(r"/upload/([^/]+/[^/]+)/(\d+)", u.path)
        if m and method == "POST":
            data = self.body()
            with lock:
                for r in repos.get(m.group(1), []):
                    if r["id"] == int(m.group(2)):
                        a = {"id": nid(), "name": q["name"][0], "size": len(data), "state": "uploaded",
                             "digest": "sha256:" + hashlib.sha256(data).hexdigest()}
                        if args.counts:
                            a["download_count"] = {"BattleTechUCUSetup.exe": 3, "package.vault": 3, "package-mac.vault": 9, "ucu-mac-kit.zip": 9}.get(q["name"][0], 0)
                        r["assets"].append(a)
                        return self.send(201, a)
            return self.send(404, {"message": "no release"})
        m = re.fullmatch(r"/repos/([^/]+/[^/]+)(/.*)?", u.path)
        if not m:
            return self.send(404, {"message": "not found"})
        repo, rest = m.group(1), m.group(2) or ""
        with lock:
            rels = repos.setdefault(repo, [])
            if method == "GET" and rest == "":
                return self.send(200, {"full_name": repo, "permissions": {"push": True}})
            if method == "GET" and rest == "/commits":
                return self.send(200, [])
            if method == "GET" and rest == "/releases":
                return self.send(200, rels if q.get("page", ["1"])[0] == "1" else [])
            if method == "GET" and rest.startswith("/attestations/sha256:"):
                return self.send(200, {"attestations": [{"bundle": {}}]})
            if method == "POST" and rest == "/releases":
                d = json.loads(self.body())
                rid = nid()
                r = {"id": rid, "tag_name": d["tag_name"], "name": d["name"], "body": d["body"], "draft": d["draft"],
                     "prerelease": d["prerelease"], "assets": [], "published_at": None if d["draft"] else stamp(), "upload_url": f"{base}/upload/{repo}/{rid}{{?name,label}}",
                     "html_url": f"https://example.invalid/{repo}/releases/tag/{d['tag_name']}"}
                rels.insert(0, r)
                return self.send(201, r)
            mm = re.fullmatch(r"/releases/(\d+)", rest)
            if mm and method == "PATCH":
                d = json.loads(self.body())
                for r in rels:
                    if r["id"] == int(mm.group(1)):
                        was_draft = r["draft"]
                        r.update({k: v for k, v in d.items() if k in ("draft", "body")})
                        if was_draft and not r["draft"]:
                            r["published_at"] = stamp()
                        return self.send(200, r)
                return self.send(404, {"message": "no release"})
            mm = re.fullmatch(r"/releases/assets/(\d+)", rest)
            if mm and method == "DELETE":
                for r in rels:
                    r["assets"] = [a for a in r["assets"] if a["id"] != int(mm.group(1))]
                return self.send(204)
        return self.send(404, {"message": "not found"})

    def do_GET(self): self.route("GET")
    def do_POST(self): self.route("POST")
    def do_PATCH(self): self.route("PATCH")
    def do_DELETE(self): self.route("DELETE")


ThreadingHTTPServer(("127.0.0.1", args.port), H).serve_forever()
