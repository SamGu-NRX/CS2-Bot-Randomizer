namespace BotRandomizer;

// Separate from the bot roller so human rerolls never consume bot randomization state.
internal sealed class PlayerMusicKitSelection(IReadOnlyList<int> kits)
{
    private readonly HashSet<int> _knownKits = new(kits);
    private int _randomKit;

    internal int Resolve(int mode)
    {
        if (mode == -1)
        {
            if (_randomKit == 0)
                Reroll();
            return _randomKit;
        }

        return mode > 0 && mode <= ushort.MaxValue && _knownKits.Contains(mode) ? mode : 0;
    }

    // Values Resolve can act on: off, random, or a kit ID in the catalog.
    internal bool IsKnownMode(int mode) => mode is 0 or -1 || _knownKits.Contains(mode);

    internal void ResetMap() => _randomKit = 0;

    internal void Reroll()
    {
        if (kits.Count == 0)
            return;

        // A reroll chooses a different kit when the catalog offers a choice.
        var previousIndex = -1;
        for (var i = 0; i < kits.Count; i++)
        {
            if (kits[i] == _randomKit)
            {
                previousIndex = i;
                break;
            }
        }

        var index = Random.Shared.Next(kits.Count - (previousIndex >= 0 && kits.Count > 1 ? 1 : 0));
        if (kits.Count > 1 && previousIndex >= 0 && index >= previousIndex)
            index++;
        _randomKit = kits[index];
    }
}
