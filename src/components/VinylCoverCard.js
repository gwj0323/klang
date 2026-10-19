/**
 * VinylCoverCard Component
 * 左侧北欧建筑几何艺术封面与曲目元数据展示组件
 */
export class VinylCoverCard {
  constructor(mountEl) {
    this.mountEl = mountEl;
    this.track = null;
    this.isPlaying = false;
  }

  _buildNordicArtworkSVG(track) {
    const { bgPrimary, bgSecondary, accent, wood, ink } = track.palette;

    if (track.id === "bergen-mist") {
      return `
        <svg viewBox="0 0 320 320" width="100%" height="100%" xmlns="http://www.w3.org/2000/svg">
          <rect width="320" height="320" fill="${bgPrimary}"/>
          <circle cx="215" cy="98" r="54" fill="${wood}" opacity="0.48"/>
          <path d="M0 210 Q95 178 190 202 T320 188 L320 320 L0 320 Z" fill="${bgSecondary}"/>
          <path d="M0 246 Q120 225 230 244 T320 236 L320 320 L0 320 Z" fill="${accent}" opacity="0.52"/>
          <line x1="36" y1="42" x2="36" y2="278" stroke="${ink}" stroke-opacity="0.16" stroke-width="1"/>
          <line x1="36" y1="246" x2="284" y2="246" stroke="${ink}" stroke-opacity="0.16" stroke-width="1"/>
          <text x="48" y="62" fill="${ink}" fill-opacity="0.55" font-size="9.5" font-family="Inter, sans-serif" letter-spacing="2.2">60.3913° N, 5.3221° E</text>
          <text x="48" y="78" fill="${ink}" fill-opacity="0.38" font-size="8.5" font-family="Inter, sans-serif" letter-spacing="1.5">FJORD STILLNESS</text>
        </svg>
      `;
    }

    if (track.id === "oslo-atelier") {
      return `
        <svg viewBox="0 0 320 320" width="100%" height="100%" xmlns="http://www.w3.org/2000/svg">
          <rect width="320" height="320" fill="${bgPrimary}"/>
          <rect x="44" y="44" width="148" height="232" fill="${bgSecondary}" rx="3"/>
          <circle cx="192" cy="160" r="68" fill="${accent}" opacity="0.38"/>
          <rect x="118" y="92" width="140" height="140" fill="none" stroke="${ink}" stroke-opacity="0.22" stroke-width="1"/>
          <line x1="44" y1="160" x2="276" y2="160" stroke="${wood}" stroke-width="1.5" opacity="0.75"/>
          <text x="56" y="68" fill="${ink}" fill-opacity="0.55" font-size="9.5" font-family="Inter, sans-serif" letter-spacing="2">OSLO ARCHIVE III</text>
        </svg>
      `;
    }

    return `
      <svg viewBox="0 0 320 320" width="100%" height="100%" xmlns="http://www.w3.org/2000/svg">
        <rect width="320" height="320" fill="${bgPrimary}"/>
        <circle cx="160" cy="160" r="96" fill="${bgSecondary}"/>
        <circle cx="160" cy="160" r="56" fill="${accent}" opacity="0.42"/>
        <path d="M40 224 L280 224" stroke="${ink}" stroke-opacity="0.2" stroke-width="1"/>
        <path d="M72 244 L248 244" stroke="${ink}" stroke-opacity="0.14" stroke-width="1"/>
        <text x="44" y="56" fill="${ink}" fill-opacity="0.55" font-size="9.5" font-family="Inter, sans-serif" letter-spacing="2.2">64.1466° N · REYKJAVIK</text>
      </svg>
    `;
  }

  updateTrack(track) {
    this.track = track;
    this.render();
  }

  setPlaying(isPlaying) {
    this.isPlaying = isPlaying;
    const frame = this.mountEl.querySelector(".artwork-frame");
    if (frame) {
      frame.classList.toggle("is-playing", isPlaying);
    }
  }

  render() {
    if (!this.track) return;
    this.mountEl.innerHTML = `
      <div class="artwork-frame ${this.isPlaying ? "is-playing" : ""}">
        <div class="vinyl-disc-peek" aria-hidden="true"></div>
        <div class="artwork-canvas">
          ${this._buildNordicArtworkSVG(this.track)}
        </div>
      </div>

      <div class="track-editorial-meta">
        <div class="track-catalogue-Row">
          <span>${this.track.catalogueNo}</span>
        </div>
        <h1 class="track-main-title">${this.track.title}</h1>
        <p class="track-Byline">${this.track.artist} — ${this.track.album}</p>
        <div class="track-specs-pills">
          <span class="spec-pill">${this.track.format}</span>
          <span class="spec-pill">${this.track.tempo}</span>
        </div>
      </div>
    `;
  }
}
