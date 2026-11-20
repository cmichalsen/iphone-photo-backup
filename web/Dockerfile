FROM python:3.13-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
RUN pip install --no-cache-dir flask cheroot cryptography pillow pillow-heif

WORKDIR /app
COPY app.py index.html ./

ENV PHOTO_ROOT=/photos THUMB_DIR=/thumbs PYTHONUNBUFFERED=1
EXPOSE 8080 8443
CMD ["python", "app.py"]
