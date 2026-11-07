namespace WinTiles.Core;

/// <summary>An entity (or a Beszel system) to choose in the tile editor.</summary>
public sealed record PickableEntity(string Id, string Name, string State)
{
    public string Domain => HomeAssistant.Domain(Id);
}

/// <summary>Home Assistant helpers: what clicking does, how an entity reads, which symbol it gets.</summary>
public static class HomeAssistant
{
    public static string Domain(string entityId) => entityId.Split('.', 2)[0];

    /// <summary>Lights and switches first in the entity picker; diagnostics last.</summary>
    public static int Rank(string domain)
    {
        string[] order = { "light", "switch", "fan", "cover", "lock", "climate", "media_player", "scene", "script",
                           "automation", "input_boolean", "button", "sensor", "binary_sensor", "person", "weather" };
        var i = Array.IndexOf(order, domain);
        return i < 0 ? order.Length : i;
    }

    /// <summary>What clicking the tile does, or null if it just opens Home Assistant.</summary>
    public static (string Domain, string Name)? Service(string domain, string state = "") => domain switch
    {
        "light" or "switch" or "fan" or "input_boolean" or "cover" or "siren" or "humidifier" => (domain, "toggle"),
        "media_player" => (domain, "media_play_pause"),
        "automation" => (domain, "trigger"),
        "script" or "scene" => (domain, "turn_on"),
        "button" or "input_button" => (domain, "press"),
        "lock" => (domain, state == "locked" ? "unlock" : "lock"),
        _ => null,
    };

    /// <summary>Whether the entity runs something (scripts, scenes) rather than switching on and off.</summary>
    public static bool IsRunnable(string domain) => domain is "automation" or "script" or "scene" or "button" or "input_button";

    public static ServiceSnapshot Parse(J e)
    {
        var s = new ServiceSnapshot();
        var id = e["entity_id"].Str ?? "";
        var domain = Domain(id);
        var attrs = e["attributes"];
        var state = e["state"].Str ?? "unknown";
        s.Caption = attrs["friendly_name"].Str ?? id;
        var unit = attrs["unit_of_measurement"].Str;

        if (domain == "climate" && attrs["current_temperature"].Num is { } current)
        {
            s.Headline = Fmt.F(current, "0") + "°";
            if (attrs["temperature"].Num is { } target) s.Stats.Add(new("Target", Fmt.F(target, "0") + "°"));
            s.Stats.Add(new("Mode", Label(state)));
        }
        else if (double.TryParse(state, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            var digits = Math.Round(value) == value ? "0" : Math.Abs(value) < 10 ? "0.0" : "0";
            var suffix = unit is null ? "" : unit == "%" || unit.StartsWith('°') ? unit : " " + unit;
            s.Headline = Fmt.F(value, digits) + suffix;
        }
        else if (domain == "sensor" && unit is not null) s.Headline = state + " " + unit;
        else s.Headline = Label(state);

        if (domain == "light" && state == "on" && attrs["brightness"].Num is { } brightness)
            s.Gauges.Add(new("Brightness", brightness / 255, Fmt.Percent(brightness / 255)));
        if (domain == "media_player" && attrs["media_title"].Str is { } title)
            s.Stats.Add(new("Playing", string.Join(" — ", new[] { attrs["media_artist"].Str, title }.Where(x => x is not null))));
        if (Fmt.IsoDate(e["last_changed"].Str) is { } changed)
            s.Stats.Add(new("Changed", Ago(changed)));

        s.IsOn = state switch
        {
            "on" or "open" or "opening" or "playing" or "unlocked" or "home" or "heat" or "cool" or "heat_cool" or "auto" => true,
            "off" or "closed" or "closing" or "paused" or "idle" or "locked" or "not_home" or "standby" => false,
            _ => null,
        };
        if (IsRunnable(domain)) s.IsOn = null;
        s.Symbol = Symbol(domain, attrs["device_class"].Str, s.IsOn ?? false);
        return s;
    }

    public static string Label(string state) => state switch
    {
        "on" => "On",
        "off" => "Off",
        "unavailable" => "Unavailable",
        "not_home" => "Away",
        "home" => "Home",
        _ when Fmt.IsoDate(state) is not null && state.Contains('T') => "Ready",     // scenes and buttons report a timestamp
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(state.Replace('_', ' ')),
    };

    public static string Ago(DateTimeOffset when, DateTimeOffset? now = null)
    {
        var seconds = ((now ?? DateTimeOffset.UtcNow) - when).TotalSeconds;
        if (seconds < 60) return "just now";
        if (seconds < 3600) return $"{(int)(seconds / 60)} min ago";
        if (seconds < 86_400) return $"{(int)(seconds / 3600)} hr ago";
        return $"{(int)(seconds / 86_400)} d ago";
    }

    /// <summary>Monochrome symbols (drawn with the emoji font, in white).</summary>
    public static string Symbol(string domain, string? deviceClass, bool on) => domain switch
    {
        "light" => "💡",
        "switch" or "input_boolean" => "⏻",
        "fan" => "🌀",
        "cover" => "🪟",
        "lock" => on ? "🔓" : "🔒",
        "climate" => "🌡",
        "media_player" => on ? "▶" : "🔈",
        "automation" => "⚙",
        "script" => "📜",
        "scene" => "✨",
        "button" or "input_button" => "👆",
        "person" or "device_tracker" => on ? "🏠" : "🚶",
        "weather" => "⛅",
        "camera" => "📷",
        "vacuum" => "🧹",
        "binary_sensor" => deviceClass switch
        {
            "door" or "garage_door" or "opening" => "🚪",
            "window" => "🪟",
            "motion" or "occupancy" or "presence" => on ? "🏃" : "🧍",
            "moisture" => "💧",
            "smoke" => "🔥",
            _ => on ? "●" : "○",
        },
        "sensor" => deviceClass switch
        {
            "temperature" => "🌡",
            "humidity" => "💧",
            "power" or "energy" or "current" or "voltage" => "⚡",
            "battery" => "🔋",
            "illuminance" => "☀",
            _ => "📊",
        },
        _ => "🏠",
    };
}
