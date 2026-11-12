namespace ContentCoHostStudio;

// Adapted from Stream Avatar Studio: shared sprite sequencing and speech response.


public enum MouthTier { Small, Normal, Large }
public sealed class SpriteFrames
{
    public required string[] Idle { get; init; }
    public required string[] Talk { get; init; }
    public string[] Blink { get; set; } = [];
    public string[] Small { get; init; } = [];
    public string[] Large { get; init; } = [];
    public string[] Talking(MouthTier tier) => tier switch { MouthTier.Small when Small.Length > 0 => Small, MouthTier.Large when Large.Length > 0 => Large, _ => Talk };
    public bool HasTier(MouthTier tier) => tier switch { MouthTier.Small => Small.Length > 0, MouthTier.Normal => Talk.Length > 0, _ => Large.Length > 0 };
    public MouthTier[] AvailableTiers() => Enum.GetValues<MouthTier>().Where(HasTier).ToArray();
}

public sealed class SpriteCatalog
{
    public Dictionary<string, SpriteFrames> Sets { get; } = [];
    public SpriteCatalog(IEnumerable<string> resourceNames)
    {
        var names = resourceNames.ToHashSet(StringComparer.Ordinal);
        string[] Sequence(string prefix)
        {
            var list = new List<string>();
            for (var i = 1; i <= 99 && names.Contains($"Sprites.{prefix}{i:000}.png"); i++) list.Add($"Sprites.{prefix}{i:000}.png");
            if (list.Count == 0 && names.Contains($"Sprites.{prefix}.png")) list.Add($"Sprites.{prefix}.png");
            return list.ToArray();
        }
        foreach (var mood in Moods.Discover(names))
        {
            var idle = Sequence(mood.Prefix + "_idle");
            var talk = Sequence(mood.Prefix + "_talk");
            if (idle.Length == 0 || talk.Length == 0) continue;
            if (Sets.ContainsKey(mood.Id)) throw new InvalidDataException($"Multiple sprite prefixes resolve to the same mood name: {mood.Id}");
            Sets[mood.Id] = new() { Idle = idle, Talk = talk, Blink = Sequence(mood.Prefix + "_idle_blink"), Small = Sequence(mood.Prefix + "_talk_small"), Large = Sequence(mood.Prefix + "_talk_large") };
        }
        if (!Sets.ContainsKey("Normal")) throw new InvalidDataException("The bundled avatar has no Normal idle/talking frames.");
        foreach (var set in Sets.Values.Where(s => s.Blink.Length == 0)) set.Blink = Sets["Normal"].Blink;
    }
    public bool Available(string id) => Sets.ContainsKey(id);
}

public record AnimationFrame(string Resource, double X, double Y, bool Talking);
public sealed class AnimationEngine
{
    private readonly SpeechResponse speech = new();
    public float SpeechLevel => speech.Level;
    public bool Talking => speech.Talking;
    public float ProcessedInputLevel { get; private set; }
    public MouthTier? CurrentTier { get; private set; }
    public bool UsedFallback { get; private set; }
    public void ResetAudioTracking()
    {
        speech.Reset(); ProcessedInputLevel = 0; CurrentTier = null; UsedFallback = false;
        pendingTier = null; lastMouthMood = null; lastTuning = null; previousGroup = null;
        // Narration, seeking, and idle/live animation use different clocks. A
        // pending blink from the old clock must not fire on every new frame.
        nextBlink = blinkEnd = 0; lastAnimationTime = null;
    }
    private readonly Random random;
    public AnimationEngine(int? seed = null) => random = seed.HasValue ? new Random(seed.Value) : new Random();
    private MouthTier? pendingTier;
    private long pendingSince;
    private string? lastMouthMood;
    private MouthTuning? lastTuning;
    private string? previousGroup;
    private long groupStart, nextBlink, blinkEnd;
    private long? lastAnimationTime;
    public static float Threshold(int sensitivity) => .1f * (float)Math.Pow(.02, Math.Clamp(sensitivity, 0, 100) / 100d);
    public static float Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0; foreach (var v in samples) sum += v * v;
        return samples.Length == 0 ? 0 : (float)Math.Sqrt(sum / samples.Length);
    }
    public static MouthTier SelectTier(float volume, float threshold, MouthTier previous)
    {
        var ratio = volume / threshold;
        if (previous == MouthTier.Large) return ratio < 2.1f ? MouthTier.Normal : MouthTier.Large;
        if (previous == MouthTier.Small) return ratio >= 2.4f ? MouthTier.Large : ratio >= 1.55f ? MouthTier.Normal : MouthTier.Small;
        return ratio >= 2.4f ? MouthTier.Large : ratio < 1.35f ? MouthTier.Small : MouthTier.Normal;
    }
    public static (MouthTier[] Tiers, bool Fallback) EligibleTiers(SpriteFrames set, MouthTuning tuning)
    {
        tuning = tuning.Normalize();
        var available = set.AvailableTiers();
        var enabled = available.Where(tuning.Enabled).ToArray();
        if (enabled.Length > 0) return (enabled, false);
        var preferred = Enum.GetValues<MouthTier>().Where(tuning.Enabled).ToArray();
        var closest = available.OrderBy(t => preferred.Min(p => Math.Abs((int)t - (int)p)))
            .ThenBy(t => t == MouthTier.Normal ? 0 : 1).First();
        return ([closest], true);
    }

    private static MouthTier DesiredTier(double db, MouthTier current, MouthTier[] eligible, MouthTuning tuning)
    {
        if (eligible.Length == 1) return eligible[0];
        if (current == MouthTier.Large && eligible.Contains(MouthTier.Large) && db >= tuning.LargeStartsDb - 2) return current;
        if (current == MouthTier.Normal && eligible.Contains(MouthTier.Small) && db >= tuning.NormalStartsDb - 2 &&
            (!eligible.Contains(MouthTier.Large) || db < tuning.LargeStartsDb)) return current;
        if (eligible.Contains(MouthTier.Large) && db >= tuning.LargeStartsDb) return MouthTier.Large;
        if (eligible.Contains(MouthTier.Normal) && (!eligible.Contains(MouthTier.Small) || db >= tuning.NormalStartsDb)) return MouthTier.Normal;
        return eligible[0];
    }

    public AnimationFrame Next(SpriteCatalog catalog, string mood, float volume, int sensitivity, int bob, int shake, long now, MouthTuning? tuning = null, long? visualTimeMs = null)
    {
        // Speech follows narration time. Live preview visuals may use wall time so
        // a stalled media clock cannot leave a blink or idle frame frozen.
        var visualNow = visualTimeMs ?? now;
        if (lastAnimationTime is long previous && (now < previous || now - previous > 500))
            ResetAudioTracking();
        lastAnimationTime = now;
        var controls = (tuning ?? new MouthTuning()).Normalize();
        var set = catalog.Sets.GetValueOrDefault(mood) ?? catalog.Sets["Normal"];
        ProcessedInputLevel = float.IsFinite(volume) ? Math.Clamp(volume * MouthTuning.DbToLevel(controls.TrackingGainDb), 0, 1) : 0;
        speech.Update(ProcessedInputLevel, Threshold(sensitivity), now, controls.AttackMs, controls.ReleaseMs, controls.ClosingDelayMs);
        var talking = speech.Talking;
        volume = speech.Level;
        if (nextBlink == 0) nextBlink = visualNow + random.Next(2000, 5001);
        if (visualNow >= nextBlink) { blinkEnd = visualNow + 120; nextBlink = visualNow + random.Next(2000, 5001); }
        MouthTier tier = MouthTier.Normal;
        if (talking)
        {
            var (eligible, fallback) = EligibleTiers(set, controls);
            UsedFallback = fallback;
            var db = MouthTuning.LevelToDb(volume);
            if (CurrentTier is null || !eligible.Contains(CurrentTier.Value) || lastMouthMood != mood || lastTuning != controls)
            {
                // A new utterance or expression has no tier transition to hold.
                CurrentTier = DesiredTier(db, eligible[0], eligible, controls); pendingTier = null;
            }
            var desired = DesiredTier(db, CurrentTier.Value, eligible, controls);
            if (desired == CurrentTier) pendingTier = null;
            else if (controls.TierHoldMs == 0) { CurrentTier = desired; pendingTier = null; }
            else if (pendingTier != desired) { pendingTier = desired; pendingSince = now; }
            else if (now - pendingSince >= controls.TierHoldMs) { CurrentTier = desired; pendingTier = null; }
            tier = CurrentTier.Value;
            lastMouthMood = mood; lastTuning = controls;
        }
        else { CurrentTier = null; UsedFallback = false; pendingTier = null; lastMouthMood = null; }
        var blink = !talking && visualNow < blinkEnd && set.Blink.Length > 0;
        var frames = talking ? set.Talking(tier) : blink ? set.Blink : set.Idle;
        var group = mood + (talking ? tier.ToString() : blink ? "blink" : "idle");
        if (previousGroup != group) { previousGroup = group; groupStart = visualNow; }
        var interval = talking ? 100 : blink ? Math.Max(1, 120 / frames.Length) : 150;
        var frame = frames[(int)((visualNow - groupStart) / interval % frames.Length)];
        var amplitude = (2 + Math.Clamp(volume, 0, .3f) / .3 * 18) * shake / 100;
        return new(frame, talking ? (random.NextDouble() * 2 - 1) * amplitude : 0,
            talking ? (random.NextDouble() * 2 - 1) * amplitude : Math.Sin(visualNow / 1000.0 * 2 * Math.PI * .8) * 6 * bob / 100, talking);
    }
}



/// <summary>Per-project animation controls. Levels are dBFS of the tracking signal, not audible audio.</summary>
public sealed record MouthTuning
{
    public bool SmallEnabled { get; init; } = true;
    public bool NormalEnabled { get; init; } = true;
    public bool LargeEnabled { get; init; } = true;
    public int NormalStartsDb { get; init; } = -30;
    public int LargeStartsDb { get; init; } = -18;
    public int TrackingGainDb { get; init; }
    public int AttackMs { get; init; } = 10;
    public int ReleaseMs { get; init; } = 60;
    public int ClosingDelayMs { get; init; } = 90;
    public int TierHoldMs { get; init; } = 50;

    public MouthTuning Normalize()
    {
        var normal = Math.Clamp(NormalStartsDb, -60, -3);
        return this with
        {
            NormalEnabled = NormalEnabled || (!SmallEnabled && !LargeEnabled),
            NormalStartsDb = normal,
            LargeStartsDb = Math.Clamp(LargeStartsDb, normal + 3, 0),
            TrackingGainDb = Math.Clamp(TrackingGainDb, -24, 24),
            AttackMs = Math.Clamp(AttackMs, 5, 80),
            ReleaseMs = Math.Clamp(ReleaseMs, 20, 250),
            ClosingDelayMs = Math.Clamp(ClosingDelayMs, 0, 200),
            TierHoldMs = Math.Clamp(TierHoldMs, 0, 150)
        };
    }

    public bool Enabled(MouthTier tier) => tier switch
    {
        MouthTier.Small => SmallEnabled,
        MouthTier.Normal => NormalEnabled,
        _ => LargeEnabled
    };

    public static float DbToLevel(int db) => (float)Math.Pow(10, db / 20d);
    public static double LevelToDb(float level) => level > 0 && float.IsFinite(level) ? 20 * Math.Log10(level) : -70;
}



/// <summary>Time-based mouth response, independent of device callback frequency.</summary>
public sealed class SpeechResponse
{
    private long? lastTick;
    private long lastAbove;
    public float Level { get; private set; }
    public bool Talking { get; private set; }
    public void Reset() { lastTick = null; lastAbove = 0; Level = 0; Talking = false; }
    public void Update(float input, float threshold, long now, int attackMs = 10, int releaseMs = 60, int closingDelayMs = 90)
    {
        input = float.IsFinite(input) ? Math.Clamp(input, 0, 1) : 0;
        var elapsed = lastTick.HasValue ? Math.Clamp(now - lastTick.Value, 0, 250) : 33;
        lastTick = now;
        var timeConstant = input > Level ? Math.Clamp(attackMs, 5, 80) : Math.Clamp(releaseMs, 20, 250);
        Level += (input - Level) * (float)(1 - Math.Exp(-elapsed / (double)timeConstant));
        if (input >= threshold) { Talking = true; lastAbove = now; }
        else if (Talking && Level < threshold * .65f && now - lastAbove >= Math.Clamp(closingDelayMs, 0, 200)) Talking = false;
    }
    public static double Meter(float level) => level > 0 && float.IsFinite(level) ? Math.Clamp((20 * Math.Log10(level) + 70) / 70, 0, 1) : 0;
}

/// <summary>One immutable animation update shared by every presentation surface.</summary>
public sealed record AvatarOutputSnapshot(string Sprite, double X, double Y, bool Flip, double Rotation, long Sequence, string PackRevision = "", bool Visible = true);

public readonly record struct AvatarComposition(double Scale, double Radians, double CenterX, double CenterY)
{
    public static AvatarComposition Calculate(double width, double height, double spriteWidth, double spriteHeight,
        double rotation, double x, double y, double spriteScale = 1)
    {
        var radians = rotation * Math.PI / 180;
        var rotatedWidth = Math.Abs(spriteWidth * Math.Cos(radians)) + Math.Abs(spriteHeight * Math.Sin(radians));
        var rotatedHeight = Math.Abs(spriteWidth * Math.Sin(radians)) + Math.Abs(spriteHeight * Math.Cos(radians));
        var scale = rotatedWidth <= 0 || rotatedHeight <= 0 ? 0 :
            Math.Max(0, Math.Min((width - 60) / rotatedWidth, (height - 60) / rotatedHeight) * spriteScale);
        return new(scale, radians, width / 2 + x, height / 2 + y);
    }
}

public record Mood(string Id, string Prefix);
public static class Moods
{
    public static readonly Mood[] All = [new("Normal", "normal"), new("Sad", "sad"), new("Angry", "angry"), new("Happy", "happy"), new("Surprised", "surprised"), new("Shocked", "shocked"), new("Sly", "sly"), new("Excited", "excited"), new("Thinking", "thinking"), new("Confused", "confused"), new("Pointing at Audience", "pointing_at_audience"), new("Both Arms Up", "both_arms_up"), new("One Arm Up", "one_arm_up"), new("Wave", "wave"), new("Hands on Hips", "hands_on_hips"), new("Shrug", "shrug"), new("Crossed Arms", "crossed_arms"), new("Presenting to Side", "presenting_to_side"), new("Celebrating Jump", "celebrating_jump")];
    public static bool ValidPrefix(string? prefix) => prefix is { Length: >= 1 and <= 64 } &&
        prefix.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') && prefix[0] is >= 'a' and <= 'z';
    public static string Display(string prefix) => All.FirstOrDefault(m => m.Prefix == prefix)?.Id ??
        string.Join(' ', prefix.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
    public static Mood[] Discover(IEnumerable<string> resources)
    {
        var names = resources.ToHashSet(StringComparer.Ordinal);
        return names.Where(n => n.StartsWith("Sprites.", StringComparison.Ordinal) && n.EndsWith("_idle001.png", StringComparison.Ordinal))
            .Select(n => n[8..^12]).Where(ValidPrefix)
            .Where(prefix => names.Contains($"Sprites.{prefix}_talk001.png"))
            .Select(prefix => new Mood(Display(prefix), prefix))
            .OrderBy(m => m.Id == "Normal" ? 0 : 1).ThenBy(m => m.Id, StringComparer.Ordinal).ToArray();
    }
}

