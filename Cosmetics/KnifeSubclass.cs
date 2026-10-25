namespace BotRandomizer;

// A knife made with `subclass_create <id>` keeps definition index 42 (the default knife);
// only its subclass ID says which knife it is. The subclass ID is the engine's string
// token of the knife's class name: MurmurHash2 of the lowercase name with seed 0x31415926.
internal static class KnifeSubclass
{
    private static readonly (string ClassName, ushort DefIndex)[] Knives =
    [
        ("weapon_bayonet", 500),
        ("weapon_knife_css", 503),
        ("weapon_knife_flip", 505),
        ("weapon_knife_gut", 506),
        ("weapon_knife_karambit", 507),
        ("weapon_knife_m9_bayonet", 508),
        ("weapon_knife_tactical", 509),
        ("weapon_knife_falchion", 512),
        ("weapon_knife_survival_bowie", 514),
        ("weapon_knife_butterfly", 515),
        ("weapon_knife_push", 516),
        ("weapon_knife_cord", 517),
        ("weapon_knife_canis", 518),
        ("weapon_knife_ursus", 519),
        ("weapon_knife_gypsy_jackknife", 520),
        ("weapon_knife_outdoor", 521),
        ("weapon_knife_stiletto", 522),
        ("weapon_knife_widowmaker", 523),
        ("weapon_knife_skeleton", 525),
        ("weapon_knife_kukri", 526),
    ];

    private static readonly Dictionary<uint, ushort> DefIndexByToken = BuildTable();

    internal static bool TryGetDefIndex(uint subclassToken, out ushort defIndex)
        => DefIndexByToken.TryGetValue(subclassToken, out defIndex);

    private static Dictionary<uint, ushort> BuildTable()
    {
        var table = new Dictionary<uint, ushort>(Knives.Length);
        foreach (var (className, defIndex) in Knives)
            table[StringToken(className)] = defIndex;
        return table;
    }

    internal static uint StringToken(string value)
    {
        const uint seed = 0x31415926;
        const uint m = 0x5BD1E995;
        var data = System.Text.Encoding.ASCII.GetBytes(value.ToLowerInvariant());
        var h = seed ^ (uint)data.Length;
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            var k = BitConverter.ToUInt32(data, i);
            k *= m;
            k ^= k >> 24;
            k *= m;
            h *= m;
            h ^= k;
        }
        switch (data.Length - i)
        {
            case 3: h ^= (uint)data[i + 2] << 16; goto case 2;
            case 2: h ^= (uint)data[i + 1] << 8; goto case 1;
            case 1: h ^= data[i]; h *= m; break;
        }
        h ^= h >> 13;
        h *= m;
        h ^= h >> 15;
        return h;
    }
}
