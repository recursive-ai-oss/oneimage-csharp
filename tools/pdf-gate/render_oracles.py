#!/usr/bin/env python3
"""Renders page 1 of each PDF in a list with the three reference renderers, as black-box programs:
PDFium (through the pypdfium2 wheel), MuPDF (`mutool draw`) and pdf.js (in headless Chrome).

Usage: render_oracles.py <list.txt (path TAB name)> <outdir> <dpi> [--only pdfium,mutool,pdfjs] [--jobs N]
Writes <outdir>/<oracle>/<name>.png; a failed render leaves no file (and a line in <outdir>/<oracle>.errors).
Environment: ORACLES (directory with venv/, mupdf-env/ and pdfjs/ as set up by setup_oracles.sh), CHROME.
"""
import base64, concurrent.futures as cf, http.server, os, re, socketserver, subprocess, sys, threading, urllib.parse

ORACLES = os.environ["ORACLES"]  # directory holding venv/ (pypdfium2), mupdf-env/ (mutool) and pdfjs/ (pdfjs-dist)
CHROME = os.environ.get("CHROME", "google-chrome")
HERE = os.path.dirname(os.path.abspath(__file__))


def names(lines):
    out = []
    for l in lines:
        c = l.split("\t")
        out.append((c[0], c[1], c[2] if len(c) > 2 else ""))
    return out


def pdfium(job, outdir, dpi):
    code = (
        "import sys, pypdfium2 as p\n"
        "pdf = p.PdfDocument(sys.argv[1], password=sys.argv[4] or None); page = pdf[0]\n"
        "page.render(scale=float(sys.argv[3]) / 72, may_draw_forms=True).to_pil().convert('RGB').save(sys.argv[2])\n"
    )
    src, name, pw = job
    dst = os.path.join(outdir, "pdfium", name + ".png")
    r = subprocess.run([ORACLES + "/venv/bin/python", "-c", code, src, dst, str(dpi), pw], capture_output=True, text=True, timeout=120)
    return None if r.returncode == 0 and os.path.exists(dst) else (r.stderr.strip().splitlines() or ["failed"])[-1]


def mutool(job, outdir, dpi):
    src, name, pw = job
    dst = os.path.join(outdir, "mutool", name + ".png")
    r = subprocess.run([ORACLES + "/mupdf-env/bin/mutool", "draw", "-q", "-r", str(dpi)] + (["-p", pw] if pw else []) + ["-o", dst, src, "1"], capture_output=True, text=True, timeout=120)
    return None if r.returncode == 0 and os.path.exists(dst) else (r.stderr.strip().splitlines() or ["failed"])[-1]


RESULTS = {}


def pdfjs(job, outdir, dpi, port):
    src, name, pw = job
    dst = os.path.join(outdir, "pdfjs", name + ".png")
    ev = threading.Event()
    RESULTS[name] = [ev, None, None]
    url = f"http://127.0.0.1:{port}/pdfjs-page.html?dpi={dpi}&id={urllib.parse.quote(name, safe='')}&pw={urllib.parse.quote(pw, safe='')}&f=" + urllib.parse.quote("/pdfs/" + urllib.parse.quote(os.path.abspath(src)), safe="")
    prof = os.path.join(outdir, "chrome-profiles", name)
    p = subprocess.Popen([CHROME, "--headless=new", "--disable-gpu", "--no-sandbox", f"--user-data-dir={prof}", url],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
    try:
        ev.wait(120)
    finally:
        try:
            os.killpg(p.pid, 9)
        except ProcessLookupError:
            pass
        p.wait()
        subprocess.run(["rm", "-rf", prof])
    kind, body = RESULTS.pop(name)[1:]
    if kind != "png":
        return body.decode("utf-8", "replace") if body else "timeout"
    with open(dst, "wb") as f:
        f.write(body)
    return None


def serve():
    class H(http.server.SimpleHTTPRequestHandler):
        def translate_path(self, path):
            path = urllib.parse.unquote(path.split("?")[0])
            if path.startswith("/pdfs/"):
                return path[len("/pdfs"):]
            return os.path.join(ORACLES + "/pdfjs" if path.startswith("/node_modules") else HERE, path.lstrip("/"))

        def do_POST(self):
            q = urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query)
            body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
            r = RESULTS.get(q["id"][0])
            if r:
                r[1], r[2] = q["kind"][0], body
                r[0].set()
            self.send_response(204)
            self.end_headers()

        def log_message(self, *a):
            pass

    class S(socketserver.ThreadingMixIn, http.server.HTTPServer):
        daemon_threads = True

    s = S(("127.0.0.1", 0), H)
    threading.Thread(target=s.serve_forever, daemon=True).start()
    return s.server_address[1]


def main():
    lst, outdir, dpi = sys.argv[1], sys.argv[2], sys.argv[3]
    only = sys.argv[sys.argv.index("--only") + 1].split(",") if "--only" in sys.argv else ["pdfium", "mutool", "pdfjs"]
    jobs = int(sys.argv[sys.argv.index("--jobs") + 1]) if "--jobs" in sys.argv else 4
    files = names([l.strip() for l in open(lst) if l.strip()])
    port = serve()
    for o in only:
        os.makedirs(os.path.join(outdir, o), exist_ok=True)
    work = []
    fns = {"pdfium": lambda j: pdfium(j, outdir, dpi), "mutool": lambda j: mutool(j, outdir, dpi), "pdfjs": lambda j: pdfjs(j, outdir, dpi, port)}
    for o in only:
        for j in files:
            if not os.path.exists(os.path.join(outdir, o, j[1] + ".png")):
                work.append((o, j))
    errors = {o: [] for o in only}
    with cf.ThreadPoolExecutor(jobs) as ex:
        futs = {ex.submit(fns[o], j): (o, j) for o, j in work}
        for f in cf.as_completed(futs):
            o, j = futs[f]
            try:
                e = f.result()
            except Exception as ex_:  # a hung or crashed renderer is that file's failure
                e = type(ex_).__name__
            if e:
                errors[o].append(f"{j[1]}\t{e}")
    for o in only:
        with open(os.path.join(outdir, o + ".errors"), "a") as f:
            f.write("".join(x + "\n" for x in sorted(errors[o])))


main()
