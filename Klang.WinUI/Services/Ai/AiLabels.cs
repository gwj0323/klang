using System.Collections.Generic;
using System.Linq;

namespace Klang_WinUI.Services.Ai;

/// <summary>
/// 自动标签和中文描述词。Prompt 是给 CLAP 的英文描述（CLAP 只懂英文），Zh 是显示和中文检索用的。
/// </summary>
public static class AiLabels
{
    public sealed record Label(string Id, string Category, string Zh, string Prompt);

    public const string Genre = "genre", Mood = "mood", Instrument = "instrument", Vocal = "vocal";

    public static readonly Label[] Tags =
    {
        new("pop", Genre, "流行", "pop music"),
        new("rock", Genre, "摇滚", "rock music with electric guitars and drums"),
        new("hiphop", Genre, "说唱 / 嘻哈", "hip hop music with rap"),
        new("electronic", Genre, "电子", "electronic dance music"),
        new("jazz", Genre, "爵士", "jazz music"),
        new("classical", Genre, "古典", "classical music"),
        new("folk", Genre, "民谣", "folk music with acoustic guitar"),
        new("rnb", Genre, "R&B", "r&b soul music"),
        new("metal", Genre, "金属", "heavy metal music"),
        new("country", Genre, "乡村", "country music"),
        new("blues", Genre, "布鲁斯", "blues music"),
        new("reggae", Genre, "雷鬼", "reggae music"),
        new("ambient", Genre, "氛围", "ambient music"),
        new("lofi", Genre, "Lo-fi", "lo-fi hip hop beats"),
        new("punk", Genre, "朋克", "punk rock music"),
        new("funk", Genre, "放克", "funk music"),
        new("house", Genre, "浩室 / 舞曲", "house dance music"),
        new("soundtrack", Genre, "影视原声", "cinematic film soundtrack"),
        new("chinese", Genre, "中国风", "traditional chinese music"),
        new("anime", Genre, "动漫", "japanese anime song"),
        new("acoustic", Genre, "原声 / 不插电", "acoustic unplugged song"),
        new("latin", Genre, "拉丁", "latin music"),

        new("happy", Mood, "欢快", "happy cheerful music"),
        new("sad", Mood, "伤感", "sad emotional music"),
        new("calm", Mood, "平静", "calm relaxing music"),
        new("energetic", Mood, "热血", "energetic powerful music"),
        new("romantic", Mood, "浪漫", "romantic love song"),
        new("dark", Mood, "暗黑", "dark ominous music"),
        new("epic", Mood, "史诗", "epic grand music"),
        new("dreamy", Mood, "梦幻", "dreamy ethereal music"),
        new("nostalgic", Mood, "怀旧", "nostalgic retro music"),
        new("angry", Mood, "激烈", "intense aggressive music"),
        new("uplifting", Mood, "励志", "uplifting inspiring music"),
        new("chill", Mood, "松弛", "chill laid-back music"),

        new("piano", Instrument, "钢琴", "a song with piano"),
        new("guitar", Instrument, "木吉他", "a song with acoustic guitar"),
        new("eguitar", Instrument, "电吉他", "a song with electric guitar solo"),
        new("strings", Instrument, "弦乐", "a song with violins and strings"),
        new("synth", Instrument, "合成器", "a song with synthesizers"),
        new("drums", Instrument, "鼓", "a song with heavy drums"),
        new("bass", Instrument, "贝斯", "a song with deep bass"),
        new("sax", Instrument, "萨克斯", "a song with saxophone"),
        new("flute", Instrument, "笛子", "a song with flute"),
        new("cninst", Instrument, "民乐（古筝 / 二胡）", "a song with guzheng and erhu"),
        new("orchestra", Instrument, "管弦乐团", "a song played by an orchestra"),

        new("female", Vocal, "女声", "a song sung by a female singer"),
        new("male", Vocal, "男声", "a song sung by a male singer"),
        new("instrumental", Vocal, "纯音乐", "instrumental music without vocals"),
        new("choir", Vocal, "合唱", "a choir singing"),
    };

    /// <summary>场景描述：中文查询先匹配到这些描述，再用英文 prompt 去 CLAP 里找。</summary>
    public static readonly Label[] Scenes =
    {
        new("rain", "scene", "下雨天 雨夜 窗外下雨", "calm music for a rainy day"),
        new("cafe", "scene", "咖啡馆 咖啡店 下午茶", "relaxing coffee shop jazz music"),
        new("night_drive", "scene", "夜晚开车 深夜驾驶 城市夜景", "night drive synthwave music"),
        new("study", "scene", "学习 专注 写作业 工作", "calm focus music for studying"),
        new("workout", "scene", "健身 跑步 运动 燃脂", "energetic workout music"),
        new("sleep", "scene", "睡觉 助眠 失眠 睡前", "soft sleep music"),
        new("party", "scene", "派对 蹦迪 聚会 夜店", "party dance music"),
        new("beach", "scene", "夏天 海边 沙滩 度假", "summer beach music"),
        new("winter", "scene", "冬天 下雪 圣诞", "cozy winter christmas music"),
        new("breakup", "scene", "失恋 分手 心碎 想哭", "sad breakup song"),
        new("wedding", "scene", "婚礼 告白 甜蜜", "sweet romantic wedding song"),
        new("travel", "scene", "旅行 公路 出发 自由", "road trip travel music"),
        new("morning", "scene", "早晨 清晨 起床 阳光", "bright morning music"),
        new("latenight", "scene", "深夜 凌晨 孤独 emo", "lonely late night music"),
        new("forest", "scene", "森林 自然 山野 治愈", "peaceful nature music"),
        new("battle", "scene", "战斗 燃 打游戏 BOSS", "epic battle music"),
        new("retro", "scene", "复古 怀旧 八十年代 老歌", "80s retro music"),
        new("meditation", "scene", "冥想 瑜伽 放空", "meditation music"),
    };

    public static IEnumerable<Label> All => Tags.Concat(Scenes);

    public static Label? Find(string id) => Tags.FirstOrDefault(t => t.Id == id);

    public static string CategoryName(string c) => c switch
    {
        Genre => L.T("流派"),
        Mood => L.T("情绪"),
        Instrument => L.T("乐器"),
        Vocal => L.T("人声"),
        _ => c
    };
}
