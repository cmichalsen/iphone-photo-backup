"""Read-only photo gallery for the iPhone backup folder (YYYY/YYYY-MM/files)."""
import hashlib
import io
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
_months_cache = (0.0, [])


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


@app.get("/api/months")
def months():
    global _months_cache
    stamp, data = _months_cache
    if time.time() - stamp > 60 or request.args.get("refresh"):
        data = []
        # Newest year first; non-year folders such as "unknown-date" go last.
        for top in sorted(os.scandir(ROOT), key=lambda e: (e.name.isdigit(), e.name), reverse=True):
            if not top.is_dir() or top.name.startswith("."):
                continue
            subs = [e for e in os.scandir(top.path) if e.is_dir() and not e.name.startswith(".")]
            folders = [(f"{top.name}/{s.name}", s.path) for s in sorted(subs, key=lambda e: e.name, reverse=True)]
            folders.append((top.name, top.path))  # files directly in the top folder (e.g. unknown-date)
            for key, path in folders:
                count = sum(1 for e in os.scandir(path) if e.is_file() and kind_of(e.name))
                if count:
                    data.append({"key": key, "count": count})
        _months_cache = (time.time(), data)
    return jsonify(data)


@app.get("/api/list")
def list_month():
    key = request.args.get("month", "")
    parts = [p for p in key.split("/") if p]
    if not parts or len(parts) > 2 or any(p.startswith(".") for p in parts):
        abort(404)
    folder = ROOT.joinpath(*parts).resolve()
    if ROOT not in folder.parents or not folder.is_dir():
        abort(404)
    items = []
    for e in os.scandir(folder):
        kind = kind_of(e.name) if e.is_file() else None
        if kind:
            items.append({"name": e.name, "kind": kind, "size": e.stat().st_size})
    items.sort(key=lambda i: i["name"])
    return jsonify(items)


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


if __name__ == "__main__":
    from waitress import serve
    THUMBS.mkdir(parents=True, exist_ok=True)
    print(f"Serving {ROOT} on port 8080 (video thumbnails: {'on' if FFMPEG else 'off, ffmpeg missing'})", flush=True)
    serve(app, host="0.0.0.0", port=int(os.environ.get("PORT", "8080")), threads=12)
