/**
 * 北欧极简曲目与逐词/音节时间戳歌词数据
 * 每行歌词均提供精确起止时间与音节切分，支持整行平滑过渡与逐字柔光推进。
 */

/**
 * 辅助函数：将一句歌词按词/音节自动生成平滑的时间切片
 */
function buildSyllables(text, startTime, endTime) {
  const isCJK = /[\u4e00-\u9fa5]/.test(text);
  const rawTokens = isCJK
    ? text.match(/[\u4e00-\u9fa5]|[a-zA-Z0-9]+|\s+|[^\s\u4e00-\u9fa5a-zA-Z0-9]+/g) || [text]
    : text.match(/\S+\s*/g) || [text];

  const totalSpan = Math.max(0.6, (endTime - startTime) * 0.88);
  const count = rawTokens.length;
  const step = totalSpan / count;

  return rawTokens.map((token, idx) => ({
    text: token,
    start: Number((startTime + idx * step).toFixed(2)),
    end: Number((startTime + (idx + 1) * step).toFixed(2)),
  }));
}

function createLyricList(rawLines) {
  return rawLines.map((item, idx) => {
    const nextStart = idx < rawLines.length - 1 ? rawLines[idx + 1].time : item.time + 5.5;
    const endTime = item.end || Math.max(item.time + 1.8, nextStart - 0.25);
    return {
      id: `line-${idx}`,
      index: idx,
      time: item.time,
      endTime,
      text: item.text,
      sub: item.sub || "",
      isInterlude: Boolean(item.isInterlude),
      syllables: item.isInterlude ? [] : buildSyllables(item.text, item.time, endTime),
    };
  });
}

export const NORDIC_TRACKS = [
  {
    id: "bergen-mist",
    catalogueNo: "NRD-041 · BERGEN RECORDINGS",
    title: "Morgendugg over Fjorden",
    subtitle: "Morning Dew Across the Quiet Fjord",
    artist: "Soren Lindqvist & Astrid Holm",
    album: "Stillhet i Nord (Architecture of Silence)",
    year: "2026",
    format: "24-Bit / 96kHz FLAC",
    tempo: "68 BPM · Felt Piano & Cello",
    duration: 76,
    palette: {
      bgPrimary: "#E7EAE6",
      bgSecondary: "#D7DFDA",
      accent: "#6E8277",
      wood: "#C9BBAE",
      ink: "#26292E",
    },
    chordProgression: [
      [220.0, 261.63, 329.63, 392.0],  // Am7
      [174.61, 220.0, 261.63, 349.23], // Fmaj7
      [130.81, 196.0, 246.94, 329.63], // Cmaj7
      [146.83, 220.0, 261.63, 329.63], // Dm9
    ],
    lyrics: createLyricList([
      { time: 0.0, text: "· · ·", sub: "Felt piano & distant fjord wind", isInterlude: true },
      { time: 4.5, text: "Pale light settles on the slate-grey water", sub: "微光静静落向板岩灰色的峡湾水面" },
      { time: 10.2, text: "Pine needles hold the weight of morning mist", sub: "松针托起清晨薄雾微凉的重量" },
      { time: 16.0, text: "Nothing hurries through the birchwood valley", sub: "白桦林谷之中，万物皆不仓促" },
      { time: 21.8, text: "Only the slow tide breathing against stone", sub: "唯有缓慢的潮汐，轻叩冷冽的礁石" },
      { time: 27.5, text: "We leave the windows open to the north wind", sub: "我们向北风敞开木窗，任清冽穿堂而过" },
      { time: 33.4, text: "White linen drifting in a quiet room", sub: "素白亚麻窗纱在静谧的房间里微微浮动" },
      { time: 39.2, text: "Every unspoken word becomes transparent", sub: "每一句未曾言说的话语，都澄澈如水" },
      { time: 45.0, text: "· · ·", sub: "Solo cello resonance in birch hall", isInterlude: true },
      { time: 50.5, text: "Shadows lengthen across untreated timber", sub: "漫射的光影掠过未上漆的浅色原木" },
      { time: 56.4, text: "Cold air carries the scent of wet moss", sub: "清冷空气里弥漫着湿润苔藓与雪松的气息" },
      { time: 62.2, text: "Stay until the horizon turns to silver", sub: "请驻足片刻，直到远方天际化作柔和的银灰" },
      { time: 68.5, text: "In the gentle geometry of winter light", sub: "在这冬日光影克制而温柔的秩序之中" },
    ]),
  },
  {
    id: "oslo-atelier",
    catalogueNo: "NRD-042 · OSLO ATELIER",
    title: "Kveldssol i Bjørkeskog",
    subtitle: "Late Sun Through Pale Birch",
    artist: "Freja Lind & Kasper Vestergaard",
    album: "Nordic Minimalist Sessions Vol. III",
    year: "2026",
    format: "PCM 48kHz · Studio Master",
    tempo: "74 BPM · Acoustic Harmonics",
    duration: 68,
    palette: {
      bgPrimary: "#EAE6DF",
      bgSecondary: "#DED8CE",
      accent: "#7B847E",
      wood: "#BFAFB2",
      ink: "#2A2D32",
    },
    chordProgression: [
      [196.0, 246.94, 293.66, 369.99], // Gmaj7
      [164.81, 220.0, 246.94, 329.63], // Em7
      [130.81, 164.81, 196.0, 246.94], // Cmaj7
      [146.83, 196.0, 220.0, 293.66],  // Dadd11
    ],
    lyrics: createLyricList([
      { time: 0.0, text: "· · ·", sub: "Warm birch acoustic overtones", isInterlude: true },
      { time: 4.0, text: "Dust motes float in a beam of amber frost", sub: "微尘在琥珀色的清霜光束中缓慢悬浮" },
      { time: 9.8, text: "Ceramic cups still warm upon the table", sub: "粗陶杯盏在原木桌面上尚存余温" },
      { time: 15.6, text: "Footsteps fade along the gravel courtyard", sub: "脚步声沿着碎石庭院渐行渐远" },
      { time: 21.4, text: "Silence is not empty, it is full of space", sub: "寂静并非空无一物，而是盛满了呼吸的留白" },
      { time: 27.2, text: "A single line drawn across handmade paper", sub: "手工棉纸上轻轻落下一道克制的线条" },
      { time: 33.0, text: "Sage-green hills beneath a porcelain sky", sub: "灰鼠尾草绿的山丘枕在白瓷般的天幕之下" },
      { time: 39.0, text: "Time unfolds without a sharp edge", sub: "时间缓缓舒展，不带一丝生硬的棱角" },
      { time: 45.2, text: "Let the evening settle like soft snow", sub: "任暮色如初雪般悄然落定" },
      { time: 51.5, text: "Breathing slow between two quiet notes", sub: "在两个轻柔音符的间隙里，自在呼吸" },
      { time: 58.0, text: "Until only warmth remains in the room", sub: "直至室内唯余一抹温润的微光" },
    ]),
  },
  {
    id: "reykjavik-rain",
    catalogueNo: "NRD-043 · REYKJAVIK CHAMBER",
    title: "Regn på Skiferstein",
    subtitle: "Soft Rain on Slate Roofs",
    artist: "Einar Sigurdsson",
    album: "Monochrome Coastlines",
    year: "2026",
    format: "Dolby Atmos · Spatial Mix",
    tempo: "62 BPM · Ambient Chamber",
    duration: 64,
    palette: {
      bgPrimary: "#E3E7E9",
      bgSecondary: "#D2D9DD",
      accent: "#65787E",
      wood: "#BDB6AE",
      ink: "#24282D",
    },
    chordProgression: [
      [174.61, 220.0, 261.63, 329.63], // Fmaj7
      [146.83, 174.61, 220.0, 261.63], // Dm7
      [196.0, 246.94, 293.66, 349.23], // G7sus4
      [130.81, 164.81, 196.0, 261.63], // C
    ],
    lyrics: createLyricList([
      { time: 0.0, text: "· · ·", sub: "Fine rain & muted string harmonics", isInterlude: true },
      { time: 4.2, text: "Raindrops trace the grain of weathered slate", sub: "细雨沿着风化板岩的纹理缓缓滑落" },
      { time: 10.0, text: "Harbor lanterns glow through coastal fog", sub: "港口的风灯在海岸雾气中泛着柔和微光" },
      { time: 16.0, text: "No neon glare, only the hush of grey water", sub: "没有刺眼的霓虹，唯有灰水低语的安宁" },
      { time: 22.0, text: "Pages turn beside a frosted windowpane", sub: "结霜的窗畔，书页被轻轻翻动" },
      { time: 28.0, text: "Every echo softens into wool and cedar", sub: "每一声回响都被羊毛与雪松温柔吸纳" },
      { time: 34.2, text: "We measure hours by the drifting clouds", sub: "我们以流云的舒卷丈量漫长的午后" },
      { time: 40.5, text: "Calm as the deep current beneath the ice", sub: "沉静如冰层之下缓缓涌动的暗流" },
      { time: 47.0, text: "Holding stillness in the palm of our hands", sub: "将这一刻的静谧轻捧于掌心" },
      { time: 54.0, text: "Where the northern sea meets the pale sky", sub: "在北国海面与苍白天空相接的尽头" },
    ]),
  },
];
