# server/server.py
import os, io
import requests
import numpy as np
import soundfile as sf
import time
from fastapi import FastAPI
from fastapi.responses import StreamingResponse
from pydantic import BaseModel

from .rvc_wrapper import RVCConverter

VOICEVOX_URL = os.environ.get("VOICEVOX_URL", "http://127.0.0.1:50021")

# === RVC 초기화 (서버 기동 시 1회) ===
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
rvc = RVCConverter(
    rvc_root=os.path.join(ROOT, "third_party", "RVC"),
    pth_path=os.path.join(ROOT, "models", "rvc", "Fern_e300_s1800.pth"),
    index_path=os.path.join(ROOT, "models", "rvc", "added_IVF75_Flat_nprobe_1_Fern_v2.index"),
    device="cuda",
    input_sr=24000,
    f0_method="rmvpe",
    index_rate=0.5,
    protect=0.6,
    filter_radius=9,
    rms_mix_rate=0.3,
    f0_up_key=0,
    resample_sr=24000,
    is_half=True,
    bucketing=False,
)

app = FastAPI()

DEFAULT_SPEAKER_ID = 2  # ずんだもん ノーマル. 여성톤이면 2(四国めたん ノーマル)

session = requests.Session()
session.headers.update({"accept": "application/json"})

def _ms(sec: float) -> str:
    return str(int(round(sec * 1000)))

class TTSIn(BaseModel):
    text: str
    speaker: int | None = None
    speed: float | None = 1.0
    pitch: float | None = 0.0
    intonation: float | None = 1.0
    rvc_enable: bool | None = True

def wav_bytes_to_float32(wav_bytes: bytes) -> tuple[np.ndarray, int]:
    buf = io.BytesIO(wav_bytes)
    pcm, sr = sf.read(buf, dtype="float32", always_2d=False)
    if pcm.ndim == 2:
        pcm = pcm.mean(axis=1)
    return pcm, sr

def float32_to_wav_bytes(pcm: np.ndarray, sr: int) -> bytes:
    buf = io.BytesIO()
    sf.write(buf, pcm, sr, format="WAV", subtype="PCM_16")
    return buf.getvalue()

@app.post("/tts")
def tts_once(body: TTSIn):
    spk = body.speaker if body.speaker is not None else DEFAULT_SPEAKER_ID
    t0 = time.perf_counter()

    # audio_query
    t = time.perf_counter()
    q = requests.post(f"{VOICEVOX_URL}/audio_query",
                      params={"text": body.text, "speaker": spk}, timeout=10).json()
    q["outputSamplingRate"] = int(getattr(rvc, "tgt_sr", 24000))
    q["outputStereo"] = False
    q["speedScale"] = float(body.speed or 1.0)
    t_audio_query = time.perf_counter() - t

    # synthesis
    t = time.perf_counter()
    wav_bytes = requests.post(f"{VOICEVOX_URL}/synthesis",
                              params={"speaker": spk}, json=q, timeout=30).content
    t_synth = time.perf_counter() - t

    # RVC convert (옵션)
    t = time.perf_counter()
    if body.rvc_enable:
        pcm, sr = wav_bytes_to_float32(wav_bytes)
        conv_pcm, out_sr = rvc.convert(pcm, sr=sr)
        wav_bytes = float32_to_wav_bytes(conv_pcm, sr=out_sr)
    t_rvc = time.perf_counter() - t

    total = time.perf_counter() - t0

    # 콘솔 로그
    print(f"[TIME] audio_query={_ms(t_audio_query)} ms | "
          f"synth={_ms(t_synth)} ms | rvc={_ms(t_rvc)} ms | total={_ms(total)} ms")

    # 응답 헤더에도 넣기
    headers = {
        "X-Query-ms": _ms(t_audio_query),
        "X-Synth-ms": _ms(t_synth),
        "X-RVC-ms": _ms(t_rvc),
        "X-Total-ms": _ms(total),
    }
    return StreamingResponse(iter([wav_bytes]), media_type="audio/wav", headers=headers)
