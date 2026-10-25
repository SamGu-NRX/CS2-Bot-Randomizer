using System.Globalization;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;

namespace BotRandomizer;

// CounterStrikeSharp 1.0.378's FakeConVar.ExecuteCommand returns early for every player caller.
// A listen server's host types into a player console, so the host could only change plugin
// settings through cfgs the server itself executes. A command listener on the same name lets the
// host set them; dedicated servers keep FakeConVar's behaviour.
internal static class HostConsole
{
    internal enum Caller
    {
        // Leave the command to FakeConVar, which ignores players.
        Ignore,
        Host,
        OtherPlayer,
        // A 127.0.0.1 caller before any server_spawn event, e.g. after a mid-map hot reload.
        Unconfirmed,
    }

    // CounterStrikeSharp exposes no dedicated-server check. dedicatedServer is the server_spawn
    // event's "dedicated" field, null until that event fires. The host is the human whose network
    // ID is the local client: Source 2 lists it as "loopback", and CounterStrikeSharp's own
    // listen-client check (PlayerManager::OnClientConnect_Post) matches "127.0.0.1". No remote
    // client can be "loopback", but a client on a dedicated server's machine can be 127.0.0.1.
    internal static Caller Classify(bool? dedicatedServer, string? networkId)
    {
        if (dedicatedServer == true)
            return Caller.Ignore;
        if (networkId == "loopback")
            return Caller.Host;
        var localAddress = networkId is not null
            && (networkId == "127.0.0.1" || networkId.StartsWith("127.0.0.1:", StringComparison.Ordinal));
        if (dedicatedServer == false)
            return localAddress ? Caller.Host : Caller.OtherPlayer;
        return localAddress ? Caller.Unconfirmed : Caller.Ignore;
    }

    internal static Caller Classify(CCSPlayerController player, bool? dedicatedServer)
        => player is { IsValid: true, IsBot: false, IsHLTV: false }
            ? Classify(dedicatedServer, player.IpAddress)
            : Caller.Ignore;

    // Accepts what FakeConVar accepts for these types: integers, and 0/1/true/false for bools.
    internal static bool TryParse<T>(string text, out T value) where T : IParsable<T>
    {
        text = text.Trim();
        if (typeof(T) == typeof(bool) && text is "0" or "1")
        {
            value = (T)(object)(text == "1");
            return true;
        }

        return T.TryParse(text, CultureInfo.InvariantCulture, out value!);
    }

    // Command listener body for a FakeConVar's name. Returns Handled once it has replied.
    // validate returns an error message for a parsed value the setting must reject.
    internal static HookResult HandleConVar<T>(
        CCSPlayerController? player,
        CommandInfo command,
        FakeConVar<T> convar,
        bool? dedicatedServer,
        Func<T, string?>? validate = null)
        where T : IComparable<T>, IParsable<T>
    {
        // The server console and server-run cfgs have no player; FakeConVar already applied them.
        if (player is null)
            return HookResult.Continue;

        switch (Classify(player, dedicatedServer))
        {
            case Caller.Ignore:
                return HookResult.Continue;
            case Caller.OtherPlayer:
                command.ReplyToCommand($"{convar.Name}: only the listen-server host can change this.");
                return HookResult.Handled;
            case Caller.Unconfirmed:
                command.ReplyToCommand(
                    $"{convar.Name}: change the map once so BotRandomizer can confirm this is a listen server.");
                return HookResult.Handled;
        }

        if (command.ArgCount < 2)
        {
            command.ReplyToCommand($"{convar.Name} = {convar.Value}");
            return HookResult.Handled;
        }

        var argument = command.GetArg(1);
        if (!TryParse<T>(argument, out var value))
        {
            command.ReplyToCommand($"{convar.Name}: '{argument}' is not a valid {typeof(T).Name}.");
            return HookResult.Handled;
        }

        if (validate?.Invoke(value) is { } error)
        {
            command.ReplyToCommand($"{convar.Name}: {error}");
            return HookResult.Handled;
        }

        convar.Value = value;
        command.ReplyToCommand($"{convar.Name} = {convar.Value}");
        return HookResult.Handled;
    }
}
