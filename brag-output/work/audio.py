"""HerdMe brag soundtrack: 2016 startup-launch ukulele pop, C major, 112 bpm.
Music + SFX written as one piece. Output: work/audio.wav (48 kHz stereo)."""
import numpy as np, wave, functools

SR = 48000
BPM = 112
BEAT = 60 / BPM
DUR = 42 * BEAT
N = int(DUR * SR) + 1
rng = np.random.default_rng(7)

L = np.zeros(N); R = np.zeros(N)
SEND = np.zeros(N)  # mono reverb send

def midi(m): return 440.0 * 2 ** ((m - 69) / 12)
NOTE = {n: i for i, n in enumerate(["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"])}
def m(name):  # "C4" -> midi
    return 12 * (int(name[-1]) + 1) + NOTE[name[:-1]]

def add(sig, t, gain=1.0, pan=0.0, send=0.2):
    i = int(round(t * SR))
    if i >= N: return
    sig = sig[: N - i] * gain
    lg = np.cos((pan + 1) * np.pi / 4); rg = np.sin((pan + 1) * np.pi / 4)
    L[i:i + len(sig)] += sig * lg * 1.414
    R[i:i + len(sig)] += sig * rg * 1.414
    SEND[i:i + len(sig)] += sig * send

def onepole_lp(x, a):
    y = np.empty_like(x); s = 0.0
    for i in range(len(x)):
        s += a * (x[i] - s); y[i] = s
    return y

# ---------- instruments ----------
@functools.lru_cache(None)
def pluck(mn, dur=1.6, bright=0.5):
    """Karplus-Strong, block-vectorised."""
    f = midi(mn); Nd = int(round(SR / f))
    total = int(dur * SR)
    buf = rng.uniform(-1, 1, Nd)
    buf = onepole_lp(buf, bright)  # soften the pick
    buf -= buf.mean()
    out = [buf]; prev = buf; last_pp = 0.0
    decay = 0.996
    while sum(len(b) for b in out) < total:
        shifted = np.concatenate(([last_pp], prev[:-1]))
        nb = decay * 0.5 * (prev + shifted)
        last_pp = prev[-1]; prev = nb; out.append(nb)
    y = np.concatenate(out)[:total]
    env = np.minimum(1, np.linspace(0, dur, total) / 0.002)
    y *= env * np.exp(-np.linspace(0, dur, total) * 1.6)
    return y * 0.5

CHORDS = {  # uke voicings (GCEA re-entrant)
    "C": ["G4", "C4", "E4", "C5"], "G": ["G4", "D4", "G4", "B4"],
    "Am": ["A4", "C4", "E4", "A4"], "F": ["A4", "C4", "F4", "A4"],
}
ROOT = {"C": "C3", "G": "G2", "Am": "A2", "F": "F2"}

def strum(chord, t, gain=0.5, down=True, spread=0.011):
    notes = CHORDS[chord] if down else CHORDS[chord][::-1]
    for k, n in enumerate(notes):
        add(pluck(m(n), 1.4 if down else 0.7, 0.55 if down else 0.4), t + k * spread,
            gain * (1.0 if down else 0.6) * (1 - 0.06 * k), pan=-0.28, send=0.18)

@functools.lru_cache(None)
def glock(mn, dur=1.4):
    f = midi(mn); tt = np.arange(int(dur * SR)) / SR
    y = (np.sin(2 * np.pi * f * tt) * np.exp(-tt * 3.2)
         + 0.35 * np.sin(2 * np.pi * f * 2.76 * tt) * np.exp(-tt * 9)
         + 0.12 * np.sin(2 * np.pi * f * 5.40 * tt) * np.exp(-tt * 18))
    return y * np.minimum(1, tt / 0.0015) * 0.45

@functools.lru_cache(None)
def bass(mn, dur):
    f = midi(mn); tt = np.arange(int(dur * SR)) / SR
    y = np.sin(2 * np.pi * f * tt) + 0.15 * np.sin(4 * np.pi * f * tt)
    env = np.minimum(1, tt / 0.01) * np.exp(-tt * 2.2) * np.minimum(1, (dur - tt) / 0.04)
    return np.tanh(1.4 * y * env) * 0.5

@functools.lru_cache(None)
def kick():
    tt = np.arange(int(0.3 * SR)) / SR
    ph = 2 * np.pi * np.cumsum(48 + 80 * np.exp(-tt * 30)) / SR
    return np.sin(ph) * np.exp(-tt * 13) * 0.8

@functools.lru_cache(None)
def clap():
    n = int(0.22 * SR); tt = np.arange(n) / SR
    noise = rng.uniform(-1, 1, n)
    bp = onepole_lp(noise, 0.35) - onepole_lp(noise, 0.06)  # crude band-pass ~1-2 kHz
    env = np.zeros(n)
    for off in (0.0, 0.009, 0.018):
        k = int(off * SR); env[k:] += np.exp(-(tt[: n - k]) * (60 if off < 0.018 else 22))
    return bp * env * 0.55

@functools.lru_cache(None)
def tick():
    n = int(0.03 * SR); tt = np.arange(n) / SR
    x = rng.uniform(-1, 1, n); x = x - onepole_lp(x, 0.25)  # high-passed
    return x * np.exp(-tt * 260) * 0.35

@functools.lru_cache(None)
def thump():
    tt = np.arange(int(0.9 * SR)) / SR
    ph = 2 * np.pi * np.cumsum(midi(m("C2")) * (1 + 0.8 * np.exp(-tt * 25))) / SR
    body = np.sin(ph) * np.exp(-tt * 4.5)
    n = rng.uniform(-1, 1, len(tt)); n = onepole_lp(n, 0.03) * np.exp(-tt * 18) * 2.5
    return np.tanh(1.5 * (body + n)) * 0.75

@functools.lru_cache(None)
def whoosh(dur=0.55):
    n = int(dur * SR); tt = np.arange(n) / SR
    x = rng.uniform(-1, 1, n); y = np.empty(n); s = 0.0
    for i in range(n):
        a = 0.02 + 0.18 * np.sin(np.pi * i / n) ** 2
        s += a * (x[i] - s); y[i] = s
    return y * np.sin(np.pi * tt / dur) ** 2 * 0.9

def b(beat): return beat * BEAT

# ---------- arrangement ----------
# Intro: typing ticks under "We're disrupting", thump + C chord on "localhost."
l1 = "We're disrupting"
for i in range(1, len(l1) + 1):
    t_i = 0.08 + (0.85 - 0.08) * (i / len(l1)) - 0.02
    if l1[i - 1] != " ":
        add(tick(), t_i, 0.35 + 0.1 * rng.uniform(), pan=0.1 * rng.uniform(-1, 1), send=0.05)
add(thump(), b(2), 0.5, send=0.12)
strum("C", b(2), 0.55)
add(glock(m("C6")), b(2), 0.22, pan=0.3, send=0.35)
add(glock(m("G5")), b(3), 0.14, pan=0.3, send=0.35)

# Groove beats 4..34 : C G Am F
PROG = ["C", "G", "Am", "F"]
PATTERN = [(0, True, 1.0), (1, True, 0.75), (1.5, False, 1), (2.5, False, 1), (3, True, 0.8), (3.5, False, 1)]
MELODY = {  # per chord: (beat-in-bar, note)
    "C": [(0, "E5"), (1.5, "G5"), (2.5, "C6")], "G": [(0, "D5"), (1.5, "G5"), (2.5, "B5")],
    "Am": [(0, "C6"), (1.5, "A5"), (2.5, "E5")], "F": [(0, "F5"), (1.5, "A5"), (2.5, "C6")],
}
bar = 0
start = 4
while start < 34:
    ch = PROG[bar % 4]
    for off, down, g in PATTERN:
        if start + off >= 34: continue
        strum(ch, b(start + off), 0.42 * g, down=down)
    for off in (0, 2):
        if start + off < 34:
            add(kick(), b(start + off), 0.55, send=0.02)
            add(bass(m(ROOT[ch]), BEAT * 1.9), b(start + off), 0.38, send=0.03)
    for off in (1, 3):
        if start + off < 34:
            add(clap(), b(start + off), 0.30, pan=0.12, send=0.25)
    if bar >= 1:  # melody enters on bar 2, soft
        for off, n in MELODY[ch]:
            if start + off < 34:
                add(glock(m(n)), b(start + off), 0.10, pan=0.35, send=0.35)
    bar += 1; start += 4

# ---------- SFX, in key, tucked under the music ----------
S3, S4, S5, S6 = b(11), b(20), b(27), b(33)
add(glock(m("G5")), S3 + 1.25, 0.16, pan=0.2, send=0.4)            # Create Laravel click
add(glock(m("C6")), S3 + 2.5, 0.12, pan=0.2, send=0.4)             # site turns Running
for i in range(17):                                                 # typing the URL
    add(tick(), S3 + 3.2 + 0.65 * (i + 1) / 17 - 0.02, 0.18, pan=0.25, send=0.05)
add(glock(m("E6")), S3 + 3.95, 0.14, pan=0.25, send=0.4)            # Secure badge: double ping
add(glock(m("G6")), S3 + 4.05, 0.12, pan=0.25, send=0.4)
for i, n in enumerate(["C5", "D5", "E5", "G5", "A5", "C6"]):         # PHP chips cascade
    add(glock(m(n)), S4 + 0.2 + i * 0.07 + 0.08, 0.10, pan=0.3, send=0.35)
for i in range(8):                                                   # service tiles: soft ticks
    add(tick(), S4 + 0.85 + i * 0.085 + 0.08, 0.12, pan=-0.2 + 0.05 * i, send=0.08)
add(whoosh(), S5 + 1.25 - 0.27, 0.22, send=0.25)                    # EN -> AR flip
for tt in (b(5), b(11), b(20), b(27)):                              # scene cuts: tiny air
    add(whoosh(0.35), tt - 0.18, 0.08, send=0.2)

# Punchline: stop at 34, thump on "There isn't one." (35), resolve at 37
add(thump(), b(35), 0.5, send=0.18)
strum("C", b(37), 0.62, spread=0.03)
add(bass(m("C2"), 3.0), b(37), 0.45, send=0.05)
add(kick(), b(37), 0.5)
for i, n in enumerate(["C5", "E5", "G5", "C6", "E6"]):
    add(glock(m(n), 2.4), b(37) + 0.12 + i * BEAT / 2, 0.13, pan=0.3, send=0.45)

# ---------- mix ----------
ir_len = int(1.3 * SR); tt = np.arange(ir_len) / SR
irL = rng.normal(0, 1, ir_len) * np.exp(-tt * 4.2); irR = rng.normal(0, 1, ir_len) * np.exp(-tt * 4.2)
irL[: int(0.018 * SR)] = 0; irR[: int(0.023 * SR)] = 0
irL = onepole_lp(irL, 0.25); irR = onepole_lp(irR, 0.25)
def conv(x, h):
    n = 1 << int(np.ceil(np.log2(len(x) + len(h))))
    return np.fft.irfft(np.fft.rfft(x, n) * np.fft.rfft(h, n), n)[: len(x)]
wetL = conv(SEND, irL); wetR = conv(SEND, irR)
k = 0.22 / max(1e-9, np.sqrt(np.mean(np.concatenate([irL, irR]) ** 2)) * np.sqrt(ir_len) / 40)
L2 = L + wetL * k * 0.065; R2 = R + wetR * k * 0.065
mix = np.stack([L2, R2], 1)
mix -= mix.mean(0)
mix *= 0.9 / np.percentile(np.abs(mix), 99.95)
mix = np.tanh(mix * 1.1) / np.tanh(1.1)
fade = int(0.25 * SR); mix[-fade:] *= np.linspace(1, 0, fade)[:, None]
mix *= 0.80  # headroom for true peak

pcm = (np.clip(mix, -1, 1) * 32767).astype("<i2")
with wave.open("audio.wav", "wb") as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR); w.writeframes(pcm.tobytes())
print("wrote audio.wav", len(pcm) / SR, "s; wet rms ratio",
      float(np.sqrt(np.mean((wetL * k * 0.02) ** 2)) / max(1e-9, np.sqrt(np.mean(L ** 2)))))
