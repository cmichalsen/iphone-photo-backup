"""Read-only photo gallery for the iPhone backup folder (YYYY/YYYY-MM/files)."""
import hashlib
import io
import json
import os
import shutil
import subprocess
import threading
import time
from pathlib import Path

from flask import Flask, abort, jsonify, request, send_file
from PIL import Image, ImageOps
from pillow_heif import register_heif_opener

register_heif_opener()

ROOT = Path(os.environ.get("PHOTO_ROOT", "/photos")).resolve()
THUMBS = Path(os.environ.get("THUMB_DIR", "/thumbs")).resolve()
IMAGE_EXT = {".heic", ".heif", ".jpg", ".jpeg", ".png", ".gif", ".webp"}
VIDEO_EXT = {".mov", ".mp4", ".m4v", ".3gp"}
MIME = {".heic": "image/heic", ".heif": "image/heif", ".mov": "video/quicktime", ".mp4": "video/mp4",
        ".m4v": "video/mp4", ".3gp": "video/3gpp"}
FFMPEG = shutil.which("ffmpeg")

app = Flask(__name__, static_folder=None)
_heavy = threading.Semaphore(3)  # limit concurrent thumbnail/preview generation
_index_cache = (0.0, [])
_index_lock = threading.Lock()


def kind_of(name: str):
    ext = os.path.splitext(name)[1].lower()
    return "image" if ext in IMAGE_EXT else "video" if ext in VIDEO_EXT else None


def resolve(rel: str) -> Path:
    """Maps a URL path to a file under ROOT; refuses anything outside it or in hidden folders."""
    parts = [p for p in rel.replace("\\", "/").split("/") if p]
    if not parts or any(p.startswith(".") for p in parts):
        abort(404)
    path = (ROOT.joinpath(*parts)).resolve()
    if ROOT not in path.parents or not path.is_file() or kind_of(path.name) is None:
        abort(404)
    return path


@app.get("/api/all")
def all_items():
    """Every photo and video in the backup: p = path, k = i/v, s = bytes, t = capture time (unix seconds)."""
    global _index_cache
    with _index_lock:
        stamp, data = _index_cache
        if time.time() - stamp > 120 or request.args.get("refresh"):
            data = build_index()
            _index_cache = (time.time(), data)
    return jsonify(data)


def capture_dates() -> dict:
    """Capture dates recorded by the backup app (the files' own timestamps are just the copy time)."""
    dates = {}
    try:
        with open(ROOT / ".photobackup" / "state.jsonl", encoding="utf-8-sig") as f:
            for line in f:
                try:
                    rec = json.loads(line)
                except ValueError:
                    continue
                if rec.get("N") and rec.get("M"):
                    # M is .NET ticks (100 ns since year 0001) in the phone's local time.
                    dates[rec["N"].replace("\\", "/")] = (rec["M"] - 621355968000000000) / 1e7
    except OSError:
        pass
    return dates


def build_index() -> list:
    dates = capture_dates()
    items = []
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = [d for d in dirnames if not d.startswith(".")]
        rel_dir = os.path.relpath(dirpath, ROOT).replace(os.sep, "/")
        for name in filenames:
            kind = kind_of(name)
            if not kind:
                continue
            rel = name if rel_dir == "." else f"{rel_dir}/{name}"
            st = os.stat(os.path.join(dirpath, name))
            items.append({"p": rel, "k": kind[0], "s": st.st_size, "t": int(dates.get(rel, st.st_mtime))})
    return items



def cached(path: Path, variant: str, make):
    """Returns a cached JPEG derived from `path`, generating it with make(dest) on first use."""
    st = path.stat()
    digest = hashlib.sha1(f"{path}|{st.st_size}|{st.st_mtime_ns}|{variant}".encode()).hexdigest()
    out = THUMBS / digest[:2] / f"{digest}.jpg"
    if not out.exists():
        out.parent.mkdir(parents=True, exist_ok=True)
        tmp = out.with_suffix(f".{threading.get_ident()}.tmp")
        try:
            with _heavy:
                make(tmp)
            os.replace(tmp, out)
        except Exception:
            tmp.unlink(missing_ok=True)
            abort(404)
    return send_file(out, mimetype="image/jpeg", max_age=86400 * 30)


def image_jpeg(src: Path, dest: Path, size: int, quality: int):
    with Image.open(src) as im:
        im = ImageOps.exif_transpose(im)
        im.thumbnail((size, size))
        im.convert("RGB").save(dest, "JPEG", quality=quality)


def video_jpeg(src: Path, dest: Path, size: int):
    if not FFMPEG:
        raise RuntimeError("ffmpeg not available")
    frame = subprocess.run(
        [FFMPEG, "-v", "error", "-i", str(src), "-frames:v", "1", "-vf", f"scale={size}:{size}:force_original_aspect_ratio=decrease",
         "-f", "image2pipe", "-vcodec", "mjpeg", "-"],
        capture_output=True, timeout=60, check=True).stdout
    Image.open(io.BytesIO(frame)).convert("RGB").save(dest, "JPEG", quality=75)


@app.get("/thumb/<path:rel>")
def thumb(rel):
    path = resolve(rel)
    if kind_of(path.name) == "video":
        return cached(path, "t320", lambda d: video_jpeg(path, d, 320))
    return cached(path, "t320", lambda d: image_jpeg(path, d, 320, 75))


@app.get("/preview/<path:rel>")
def preview(rel):
    """Large JPEG for browsers that can't display HEIC."""
    path = resolve(rel)
    if kind_of(path.name) != "image":
        abort(404)
    return cached(path, "p2000", lambda d: image_jpeg(path, d, 2000, 85))


@app.get("/file/<path:rel>")
def original(rel):
    path = resolve(rel)
    return send_file(path, mimetype=MIME.get(path.suffix.lower()), conditional=True,
                     as_attachment=bool(request.args.get("dl")), download_name=path.name)


@app.get("/")
def index():
    return send_file(Path(__file__).with_name("index.html"), max_age=0)


@app.get("/api/config")
def config():
    # The port the HTTPS listener is published on (the page links to it for one-tap saving on iOS).
    return jsonify({"httpsPort": int(os.environ.get("HTTPS_PUBLIC_PORT", "8643"))})


def ensure_certificate():
    """Creates (or renews) a self-signed certificate, kept in the cache volume so it stays the same across restarts."""
    import datetime
    import ipaddress
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.x509.oid import NameOID

    folder = THUMBS / "tls"
    cert_path, key_path = folder / "cert.pem", folder / "key.pem"
    now = datetime.datetime.now(datetime.timezone.utc)
    if cert_path.exists() and key_path.exists():
        existing = x509.load_pem_x509_certificate(cert_path.read_bytes())
        if existing.not_valid_after_utc - now > datetime.timedelta(days=30):
            return cert_path, key_path

    names = ["localhost"] + [n.strip() for n in os.environ.get("TLS_NAMES", "").split(",") if n.strip()]
    sans = []
    for n in names:
        try:
            sans.append(x509.IPAddress(ipaddress.ip_address(n)))
        except ValueError:
            sans.append(x509.DNSName(n))
    key = ec.generate_private_key(ec.SECP256R1())
    subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Photo Backup Gallery")])
    cert = (x509.CertificateBuilder().subject_name(subject).issuer_name(subject).public_key(key.public_key())
            .serial_number(x509.random_serial_number())
            .not_valid_before(now - datetime.timedelta(days=1)).not_valid_after(now + datetime.timedelta(days=800))
            .add_extension(x509.SubjectAlternativeName(sans), critical=False)
            .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
            .sign(key, hashes.SHA256()))
    folder.mkdir(parents=True, exist_ok=True)
    key_path.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                           serialization.NoEncryption()))
    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    return cert_path, key_path


if __name__ == "__main__":
    from cheroot.ssl.builtin import BuiltinSSLAdapter
    from cheroot.wsgi import Server

    THUMBS.mkdir(parents=True, exist_ok=True)
    port, tls_port = int(os.environ.get("PORT", "8080")), int(os.environ.get("TLS_PORT", "8443"))
    print(f"Serving {ROOT}: http on {port}, https on {tls_port} "
          f"(video thumbnails: {'on' if FFMPEG else 'off, ffmpeg missing'})", flush=True)
    try:
        cert, key = ensure_certificate()
        secure = Server(("0.0.0.0", tls_port), app, numthreads=12)
        secure.ssl_adapter = BuiltinSSLAdapter(str(cert), str(key))
        threading.Thread(target=secure.start, daemon=True).start()
    except Exception as e:  # plain http still works without it
        print(f"HTTPS is off: {e}", flush=True)
    Server(("0.0.0.0", port), app, numthreads=12).start()
