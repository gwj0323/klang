/**
 * Nordic Audio & High-Precision Timeline Engine
 * 提供 60fps 级平滑时间轴驱动，并内置 Web Audio API 北欧毛毡钢琴 (Felt Piano) 与大提琴泛音合成器，
 * 点击任意歌词跳转时具备微淡入淡出防爆音处理。
 */
export class AudioEngine {
  constructor() {
    this.ctx = null;
    this.masterGain = null;
    this.isPlaying = false;
    this.isMuted = false;
    this.volume = 0.72;

    this.currentTime = 0;
    this.duration = 76;
    this.currentTrack = null;

    this._rafId = null;
    this._lastFrameTime = 0;
    this._lastChordStep = -1;
    this._listeners = new Map();
  }

  on(event, handler) {
    if (!this._listeners.has(event)) {
      this._listeners.set(event, new Set());
    }
    this._listeners.get(event).add(handler);
    return () => this._listeners.get(event)?.delete(handler);
  }

  emit(event, payload) {
    const handlers = this._listeners.get(event);
    if (!handlers) return;
    handlers.forEach((fn) => fn(payload));
  }

  loadTrack(track, autoPlay = false) {
    const wasPlaying = this.isPlaying;
    this.pause();
    this.currentTrack = track;
    this.duration = track.duration || 72;
    this.currentTime = 0;
    this._lastChordStep = -1;

    this.emit("trackchange", {
      track,
      currentTime: 0,
      duration: this.duration,
    });
    this.emit("timeupdate", {
      currentTime: 0,
      duration: this.duration,
      source: "load",
    });

    if (autoPlay || wasPlaying) {
      this.play();
    }
  }

  _ensureAudioContext() {
    if (!this.ctx) {
      const AudioCtx = window.AudioContext || window.webkitAudioContext;
      if (!AudioCtx) return;
      this.ctx = new AudioCtx();
      this.masterGain = this.ctx.createGain();
      this.masterGain.gain.value = this.isMuted ? 0 : this.volume * 0.24;
      this.masterGain.connect(this.ctx.destination);
    }
    if (this.ctx && this.ctx.state === "suspended") {
      this.ctx.resume().catch(() => {});
    }
  }

  /**
   * 触发温润的北欧毛毡钢琴 (Felt Piano) 泛音和弦
   */
  _triggerNordicChord(chordFreqs, isSeek = false) {
    if (!this.ctx || !this.masterGain || this.isMuted) return;
    const now = this.ctx.currentTime;

    chordFreqs.forEach((freq, idx) => {
      const osc = this.ctx.createOscillator();
      const subOsc = this.ctx.createOscillator();
      const filter = this.ctx.createBiquadFilter();
      const noteGain = this.ctx.createGain();

      // 正弦 + 三角波模拟毛毡弱音琴槌的柔和木质感
      osc.type = "sine";
      osc.frequency.setValueAtTime(freq, now);

      subOsc.type = "triangle";
      subOsc.frequency.setValueAtTime(freq * 0.5, now);

      // 低通滤波切除高频毛刺，保留温暖室内混响感
      filter.type = "lowpass";
      filter.frequency.setValueAtTime(540 + idx * 90, now);

      const stagger = isSeek ? idx * 0.04 : idx * 0.14;
      const peakGain = (0.14 - idx * 0.02) * (isSeek ? 0.8 : 1);

      noteGain.gain.setValueAtTime(0.0001, now + stagger);
      noteGain.gain.linearRampToValueAtTime(peakGain, now + stagger + 0.35);
      noteGain.gain.exponentialRampToValueAtTime(0.0008, now + stagger + 4.2);

      osc.connect(filter);
      subOsc.connect(filter);
      filter.connect(noteGain);
      noteGain.connect(this.masterGain);

      osc.start(now + stagger);
      subOsc.start(now + stagger);
      osc.stop(now + stagger + 4.3);
      subOsc.stop(now + stagger + 4.3);
    });
  }

  play() {
    if (this.isPlaying) return;
    this._ensureAudioContext();

    if (this.currentTime >= this.duration - 0.1) {
      this.currentTime = 0;
      this._lastChordStep = -1;
    }

    this.isPlaying = true;
    this._lastFrameTime = performance.now();
    this.emit("playstate", { isPlaying: true });

    const tick = (now) => {
      if (!this.isPlaying) return;
      const dt = Math.min(0.1, (now - this._lastFrameTime) / 1000);
      this._lastFrameTime = now;

      this.currentTime = Math.min(this.duration, this.currentTime + dt);

      // 每 4.5 秒推进一次北欧环境毛毡钢琴和弦
      const chordStep = Math.floor(this.currentTime / 4.5);
      if (chordStep !== this._lastChordStep && this.currentTrack?.chordProgression) {
        this._lastChordStep = chordStep;
        const chords = this.currentTrack.chordProgression;
        const chord = chords[chordStep % chords.length];
        this._triggerNordicChord(chord, false);
      }

      this.emit("timeupdate", {
        currentTime: this.currentTime,
        duration: this.duration,
        source: "raf",
      });

      if (this.currentTime >= this.duration) {
        this.pause();
        this.emit("ended", {});
        return;
      }

      this._rafId = requestAnimationFrame(tick);
    };

    this._rafId = requestAnimationFrame(tick);
  }

  pause() {
    if (!this.isPlaying) return;
    this.isPlaying = false;
    if (this._rafId) {
      cancelAnimationFrame(this._rafId);
      this._rafId = null;
    }
    this.emit("playstate", { isPlaying: false });
  }

  togglePlay() {
    if (this.isPlaying) {
      this.pause();
    } else {
      this.play();
    }
  }

  /**
   * 丝滑跳转到指定时间戳（例如点击任意一行歌词或拖拽进度条）
   */
  seek(targetSeconds, { autoPlayOnSeek = true, source = "seek" } = {}) {
    const clamped = Math.max(0, Math.min(this.duration, Number(targetSeconds) || 0));
    this.currentTime = clamped;
    this._lastChordStep = Math.floor(clamped / 4.5);

    if (autoPlayOnSeek && !this.isPlaying) {
      this.play();
    } else if (this.isPlaying && this.currentTrack?.chordProgression) {
      const chords = this.currentTrack.chordProgression;
      const chord = chords[this._lastChordStep % chords.length];
      this._triggerNordicChord(chord, true);
    }

    this.emit("timeupdate", {
      currentTime: this.currentTime,
      duration: this.duration,
      source,
    });
  }

  setVolume(val) {
    this.volume = Math.max(0, Math.min(1, val));
    if (this.volume > 0 && this.isMuted) {
      this.isMuted = false;
    }
    if (this.masterGain && this.ctx) {
      this.masterGain.gain.setTargetAtTime(
        this.isMuted ? 0 : this.volume * 0.24,
        this.ctx.currentTime,
        0.05
      );
    }
    this.emit("volumechange", { volume: this.volume, isMuted: this.isMuted });
  }

  toggleMute() {
    this.isMuted = !this.isMuted;
    if (this.masterGain && this.ctx) {
      this.masterGain.gain.setTargetAtTime(
        this.isMuted ? 0 : this.volume * 0.24,
        this.ctx.currentTime,
        0.05
      );
    }
    this.emit("volumechange", { volume: this.volume, isMuted: this.isMuted });
  }
}

export function formatTimecode(seconds) {
  const s = Math.max(0, Math.floor(seconds || 0));
  const mins = Math.floor(s / 60);
  const secs = s % 60;
  return `${String(mins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`;
}
