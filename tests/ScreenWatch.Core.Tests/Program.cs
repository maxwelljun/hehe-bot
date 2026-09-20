using ScreenWatch.Core;

var tests = new (string Name, Action Run)[]
{
    ("Identical image reaches 100", () => Equal(100, ImageMatcher.Compare(Solid(50, 80, 110), Solid(50, 80, 110)).Score)),
    ("Opposite colors are rejected", () => Equal(0, ImageMatcher.Compare(Solid(0, 0, 0), Solid(255, 255, 255)).Score)),
    ("Small rendering differences remain similar", () => True(ImageMatcher.Compare(Solid(90, 100, 110), Solid(92, 98, 113)).Score > 98)),
    ("Different colors cannot match through grayscale", () => True(ImageMatcher.Compare(Solid(255, 0, 0), Solid(0, 130, 0)).Score < 50)),
    ("A changed small status block is rejected at default threshold", () =>
    {
        byte[] original = Solid(255, 255, 255);
        byte[] changed = (byte[])original.Clone();
        Array.Fill<byte>(changed, 0, 0, changed.Length / 10 / 3 * 3);
        True(ImageMatcher.Compare(original, changed).Score < 95);
    }),
    ("Invalid samples fail explicitly", () =>
    {
        Throws<ArgumentException>(() => ImageMatcher.Compare([], []));
        Throws<ArgumentException>(() => ImageMatcher.Compare([1, 2], [1, 2]));
        Throws<ArgumentException>(() => ImageMatcher.Compare([1, 2, 3], [1, 2, 3, 4, 5, 6]));
    }),
    ("One transient match does not notify", () =>
    {
        var gate = Gate();
        False(Observe(gate, true, 1).ShouldNotify);
        False(Observe(gate, false, 2).ShouldNotify);
        False(Observe(gate, true, 3).ShouldNotify);
        True(Observe(gate, true, 4).ShouldNotify);
    }),
    ("A persistent match never spams after cooldown", () =>
    {
        var gate = Gate(confirm: 1);
        True(Observe(gate, true, 0).ShouldNotify);
        for (int second = 1; second < 10_000; second++) False(Observe(gate, true, second).ShouldNotify);
    }),
    ("One-frame disappearance does not rearm", () =>
    {
        var gate = Gate(confirm: 1, cooldown: 0);
        True(Observe(gate, true, 0).ShouldNotify);
        Observe(gate, false, 1);
        False(Observe(gate, true, 2).ShouldNotify);
    }),
    ("Confirmed disappearance permits a new appearance", () =>
    {
        var gate = Gate(cooldown: 0);
        Observe(gate, true, 0);
        True(Observe(gate, true, 1).ShouldNotify);
        Observe(gate, false, 2);
        Observe(gate, false, 3);
        False(Observe(gate, true, 4).ShouldNotify);
        True(Observe(gate, true, 5).ShouldNotify);
    }),
    ("Stable new appearance waits for remaining cooldown", () =>
    {
        var gate = Gate(confirm: 1, cooldown: 30);
        True(Observe(gate, true, 0).ShouldNotify);
        Observe(gate, false, 1);
        Observe(gate, false, 2);
        var result = Observe(gate, true, 3);
        False(result.ShouldNotify);
        True(result.State == DetectionState.CoolingDown);
        False(Observe(gate, true, 29.999).ShouldNotify);
        True(Observe(gate, true, 30).ShouldNotify);
    }),
    ("A pattern gone before cooldown expiry is not delivered later", () =>
    {
        var gate = Gate(confirm: 1, cooldown: 30);
        Observe(gate, true, 0);
        Observe(gate, false, 1);
        Observe(gate, false, 2);
        False(Observe(gate, true, 3).ShouldNotify);
        False(Observe(gate, false, 30).ShouldNotify);
    }),
    ("Clock regression is rejected", () =>
    {
        var gate = Gate();
        Observe(gate, true, 20);
        Throws<ArgumentOutOfRangeException>(() => Observe(gate, true, 19));
    }),
    ("Negative coordinates support secondary monitors", () =>
    {
        var region = new CaptureRegion(-1900, 40, 200, 300);
        True(region.FitsInside(-1920, 0, 1920, 1080));
        False(region.FitsInside(0, 0, 1920, 1080));
    }),
    ("Cross-monitor and oversized regions are rejected", () =>
    {
        False(new CaptureRegion(-10, 10, 200, 200).FitsInside(0, 0, 1920, 1080));
        False(new CaptureRegion(0, 0, 10, 200).IsValid);
        False(new CaptureRegion(0, 0, 8192, 8192).IsValid);
        False(new CaptureRegion(int.MaxValue - 4, 0, 200, 200).FitsInside(0, 0, 1920, 1080));
    }),
    ("Dangerous or malformed reference paths are rejected", () =>
    {
        foreach (string name in new[] { "../reference.png", @"C:\reference.png", "reference.png", "reference-zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.png", "reference-00000000000000000000000000000000.png/.." })
            Throws<ArgumentException>(() => new MonitorSettings { ReferenceFile = name }.Validate());
        new MonitorSettings { ReferenceFile = "reference-0123456789abcdef0123456789abcdef.png" }.Validate();
    }),
    ("Non-finite thresholds and invalid timings fail", () =>
    {
        foreach (double threshold in new[] { double.NaN, double.PositiveInfinity, 49, 101 })
            Throws<ArgumentException>(() => new MonitorSettings { Threshold = threshold }.Validate());
        Throws<ArgumentException>(() => new MonitorSettings { IntervalMs = 0 }.Validate());
        Throws<ArgumentException>(() => new MonitorSettings { ConfirmFrames = 0 }.Validate());
        Throws<ArgumentException>(() => new MonitorSettings { RearmFrames = 21 }.Validate());
        Throws<ArgumentException>(() => new MonitorSettings { CooldownSeconds = -1 }.Validate());
    }),
    ("Profile persists all settings and replaces existing files", () => WithStore(store =>
    {
        var settings = new MonitorSettings
        {
            Region = new(-1800, 100, 320, 180), ReferenceFile = "reference-0123456789abcdef0123456789abcdef.png",
            IntervalMs = 500, Threshold = 97.5, ConfirmFrames = 3, RearmFrames = 4, CooldownSeconds = 7,
            PlaySound = false, SaveScreenshots = false
        };
        store.Save(new());
        store.Save(settings);
        True(store.Load() == settings);
        True(Directory.GetFiles(store.DirectoryPath).Length == 1);
    })),
    ("Invalid update preserves the last valid profile", () => WithStore(store =>
    {
        var original = new MonitorSettings { Threshold = 96 };
        store.Save(original);
        Throws<ArgumentException>(() => store.Save(original with { Threshold = -1 }));
        True(store.Load() == original);
    })),
    ("Corrupted configuration is never silently overwritten", () => WithStore(store =>
    {
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.SettingsPath, "{ broken");
        Throws<System.Text.Json.JsonException>(() => store.Load());
        True(File.ReadAllText(store.SettingsPath) == "{ broken");
    })),
    ("Unknown profile versions fail safely", () => WithStore(store =>
    {
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.SettingsPath, "{\"Version\": 999}");
        Throws<ArgumentException>(() => store.Load());
    })),
    ("New installation has valid defaults", () => WithStore(store =>
    {
        var settings = store.Load();
        settings.Validate();
        True(settings.Region is null && settings.ReferenceFile is null);
    }))
};

int failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {exception}"); }
}
Console.WriteLine($"\n{tests.Length - failures}/{tests.Length} passed.");
return failures == 0 ? 0 : 1;

static DetectionGate Gate(int confirm = 2, int rearm = 2, int cooldown = 30) => new(new MonitorSettings
{ ConfirmFrames = confirm, RearmFrames = rearm, CooldownSeconds = cooldown });
static DetectionResult Observe(DetectionGate gate, bool match, double seconds) => gate.Observe(match, TimeSpan.FromSeconds(seconds));
static void True(bool value) { if (!value) throw new Exception("Expected true."); }
static void False(bool value) => True(!value);
static void Equal(double expected, double actual) { if (Math.Abs(expected - actual) > 0.000001) throw new Exception($"Expected {expected}, got {actual}."); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static byte[] Solid(byte red, byte green, byte blue)
{
    byte[] bytes = new byte[96 * 96 * 3];
    for (int i = 0; i < bytes.Length; i += 3) { bytes[i] = red; bytes[i + 1] = green; bytes[i + 2] = blue; }
    return bytes;
}
static void WithStore(Action<ProfileStore> action)
{
    string directory = Path.Combine(Path.GetTempPath(), "screenwatch-test-" + Guid.NewGuid().ToString("N"));
    try { action(new ProfileStore(directory)); }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
