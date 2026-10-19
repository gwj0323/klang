import { NORDIC_TRACKS } from "./data/tracks.js";
import { AudioEngine } from "./services/AudioEngine.js";
import { WinUITitleBar } from "./components/WinUITitleBar.js";
import { NavigationPane } from "./components/NavigationPane.js";
import { VinylCoverCard } from "./components/VinylCoverCard.js";
import { LyricsStage } from "./components/LyricsStage.js";
import { TransportBar } from "./components/TransportBar.js";

/**
 * Nordic WinUI 3 Music Player Application Coordinator
 */
class NordicPlayerApp {
  constructor() {
    this.tracks = NORDIC_TRACKS;
    this.currentTrackIndex = 0;
    this.audio = new AudioEngine();

    this._mountComponents();
    this._bindAudioEvents();
    this._bindGlobalShortcuts();

    // 初始化加载首支北欧曲目
    this.selectTrackByIndex(0, false);
  }

  _mountComponents() {
    this.titleBar = new WinUITitleBar(document.getElementById("mount-titlebar"), {
      onToggleSyllableMode: (enabled) => {
        this.lyricsStage.setSyllableMode(enabled);
      },
      onToggleTheme: (theme) => {
        if (theme === "dusk") {
          document.documentElement.setAttribute("data-theme", "dusk");
        } else {
          document.documentElement.removeAttribute("data-theme");
        }
      },
    });

    this.navPane = new NavigationPane(document.getElementById("mount-navpane"), {
      tracks: this.tracks,
      activeTrackId: this.tracks[0].id,
      onSelectTrack: (trackId) => {
        const idx = this.tracks.findIndex((t) => t.id === trackId);
        if (idx >= 0) this.selectTrackByIndex(idx, true);
      },
    });

    this.coverCard = new VinylCoverCard(document.getElementById("mount-cover"));

    this.lyricsStage = new LyricsStage(document.getElementById("mount-lyrics"), {
      onSeekToTime: (targetSeconds) => {
        this.audio.seek(targetSeconds, { autoPlayOnSeek: true, source: "seek" });
      },
      onActiveLineChange: (activeLine) => {
        this.transportBar.updateLyricSnippet(activeLine);
      },
    });

    this.transportBar = new TransportBar(document.getElementById("mount-transport"), {
      onTogglePlay: () => this.audio.togglePlay(),
      onPrevTrack: () => {
        const prevIdx = (this.currentTrackIndex - 1 + this.tracks.length) % this.tracks.length;
        this.selectTrackByIndex(prevIdx, true);
      },
      onNextTrack: () => {
        const nextIdx = (this.currentTrackIndex + 1) % this.tracks.length;
        this.selectTrackByIndex(nextIdx, true);
      },
      onSeekRatio: (ratio) => {
        const targetTime = ratio * this.audio.duration;
        this.audio.seek(targetTime, { autoPlayOnSeek: false, source: "seek" });
      },
      onVolumeChange: (val) => {
        this.audio.setVolume(val);
      },
      onToggleMute: () => {
        this.audio.toggleMute();
      },
    });
  }

  _bindAudioEvents() {
    this.audio.on("trackchange", ({ track, currentTime, duration }) => {
      this.coverCard.updateTrack(track);
      this.lyricsStage.loadLyrics(track.lyrics);
      this.transportBar.updateProgress(currentTime, duration);
      this.navPane.setActiveTrack(track.id);
    });

    this.audio.on("playstate", ({ isPlaying }) => {
      this.coverCard.setPlaying(isPlaying);
      this.transportBar.setPlayingState(isPlaying);
    });

    this.audio.on("timeupdate", ({ currentTime, duration, source }) => {
      this.transportBar.updateProgress(currentTime, duration);
      this.lyricsStage.syncTime(currentTime, source);
    });

    this.audio.on("volumechange", ({ volume, isMuted }) => {
      this.transportBar.updateVolume(volume, isMuted);
    });

    this.audio.on("ended", () => {
      const nextIdx = (this.currentTrackIndex + 1) % this.tracks.length;
      this.selectTrackByIndex(nextIdx, true);
    });
  }

  _bindGlobalShortcuts() {
    window.addEventListener("keydown", (e) => {
      if (e.code === "Space" && document.activeElement?.tagName !== "BUTTON") {
        e.preventDefault();
        this.audio.togglePlay();
      }
    });
  }

  selectTrackByIndex(index, autoPlay = false) {
    this.currentTrackIndex = index;
    const track = this.tracks[index];
    if (track) {
      this.audio.loadTrack(track, autoPlay);
    }
  }
}

window.addEventListener("DOMContentLoaded", () => {
  new NordicPlayerApp();
});
