# Karaoke separation worker. Builds for linux/arm64 (Raspberry Pi 4/5, 64-bit OS) and linux/amd64.
FROM python:3.11-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libsndfile1 \
    && rm -rf /var/lib/apt/lists/*

# CPU-only PyTorch. Versions are pinned because Demucs 4.0.1 predates newer torchaudio releases.
RUN pip install --no-cache-dir \
        torch==2.5.1 torchaudio==2.5.1 \
        --extra-index-url https://download.pytorch.org/whl/cpu \
    && pip install --no-cache-dir demucs==4.0.1 soundfile

WORKDIR /app
COPY worker.py .

# Model weights are downloaded here on first use; mount a volume to keep them.
ENV TORCH_HOME=/models
VOLUME /models

CMD ["python", "-u", "worker.py"]
