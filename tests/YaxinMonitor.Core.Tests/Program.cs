using YaxinMonitor.Core;

var tests = new (string Name, Action Run)[]
{
    ("Latest six creates opposite 10 bet", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = Candidate(engine.Observe(snapshot, Now()));
        Equal(BetSide.Player, bet.Side); Equal(10m, bet.Amount); Equal(1, bet.Attempt);
    }),
    ("Earlier six does not trigger", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1, 2]);
        False(engine.Observe(snapshot, Now()).OfType<BetRequestedEvent>().Any());
    }),
    ("Pair and lucky-six flags preserve banker result", () =>
    {
        var (engine, snapshot) = Ready([1, 17, 33, 65, 1, 1]);
        Equal(BetSide.Player, Candidate(engine.Observe(snapshot, Now())).Side);
    }),
    ("Ties are ignored inside trigger run", () =>
    {
        var (engine, snapshot) = Ready([1, 3, 1, 1, 3, 1, 1, 1]);
        Equal(10m, Candidate(engine.Observe(snapshot, Now())).Amount);
    }),
    ("Trailing tie does not create a late signal", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1, 3]);
        False(engine.Observe(snapshot, Now()).OfType<BetRequestedEvent>().Any());
    }),
    ("First win ends chase", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        var settled = snapshot with { GameSeq = 8, History = [.. snapshot.History, 2] };
        True(engine.Observe(settled, Now().AddMinutes(1)).OfType<ChaseCompletedEvent>().Single().Won);
        True(engine.State.Tables[1].ActiveChase is null);
    }),
    ("Loss advances 10 20 40 and then stops", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate first = SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        snapshot = snapshot with { GameSeq = 8, History = [.. snapshot.History, 1] };
        BetCandidate second = Candidate(engine.Observe(snapshot, Now().AddMinutes(1)));
        Equal(20m, second.Amount); Equal(2, second.Attempt);
        SubmitAndAccept(engine, second);
        snapshot = snapshot with { GameSeq = 9, History = [.. snapshot.History, 1] };
        BetCandidate third = Candidate(engine.Observe(snapshot, Now().AddMinutes(2)));
        Equal(40m, third.Amount); Equal(3, third.Attempt);
        SubmitAndAccept(engine, third);
        snapshot = snapshot with { GameSeq = 10, History = [.. snapshot.History, 1] };
        ChaseCompletedEvent completed = engine.Observe(snapshot, Now().AddMinutes(3)).OfType<ChaseCompletedEvent>().Single();
        False(completed.Won); True(engine.State.Tables[1].ActiveChase is null);
        False(engine.Observe(snapshot, Now().AddMinutes(3)).OfType<BetRequestedEvent>().Any());
    }),
    ("Tie repeats same 10 amount on next game", () =>
    {
        var (engine, snapshot) = Ready([2, 2, 2, 2, 2, 2]);
        SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        snapshot = snapshot with { GameSeq = 8, History = [.. snapshot.History, 3] };
        BetCandidate again = Candidate(engine.Observe(snapshot, Now().AddMinutes(1)));
        Equal(BetSide.Banker, again.Side); Equal(10m, again.Amount); Equal(1, again.Attempt);
    }),
    ("Duplicate snapshot never duplicates an order", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        Candidate(engine.Observe(snapshot, Now()));
        False(engine.Observe(snapshot, Now().AddSeconds(1)).OfType<BetRequestedEvent>().Any());
    }),
    ("Rejected order waits for next game", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate first = Candidate(engine.Observe(snapshot, Now()));
        engine.MarkSubmitted(first, Now()); engine.MarkRejected(first.OrderKey, -1, Now());
        False(engine.Observe(snapshot, Now().AddSeconds(1)).OfType<BetRequestedEvent>().Any());
        snapshot = snapshot with { GameSeq = 8, History = [.. snapshot.History, 3] };
        Equal(10m, Candidate(engine.Observe(snapshot, Now().AddMinutes(1))).Amount);
    }),
    ("Unknown order blocks automatic progress", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = Candidate(engine.Observe(snapshot, Now()));
        engine.MarkSubmitted(bet, Now()); engine.MarkUnknown(bet.OrderKey, Now());
        snapshot = snapshot with { GameSeq = 8, History = [.. snapshot.History, 1] };
        False(engine.Observe(snapshot, Now().AddMinutes(1)).OfType<BetRequestedEvent>().Any());
    }),
    ("New shoe resets old task", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        Candidate(engine.Observe(snapshot, Now()));
        snapshot = snapshot with { ShoeSeq = 2, GameSeq = 7, History = [2, 2, 2, 2, 2, 2] };
        Equal(BetSide.Banker, Candidate(engine.Observe(snapshot, Now().AddMinutes(1))).Side);
    }),
    ("New shoe cannot discard an unsettled order", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        snapshot = snapshot with { ShoeSeq = 2, GameSeq = 1, State = "S", RemainingMilliseconds = 0, History = [] };
        Throws<InvalidDataException>(() => engine.Observe(snapshot, Now().AddMinutes(1)));
        Equal(ChaseStatus.AwaitingSettlement, engine.State.Tables[1].ActiveChase!.Status);
    }),
    ("Shuffle may clear history before shoe number changes", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 2, 2]);
        engine.Observe(snapshot, Now());
        snapshot = snapshot with { State = "S", RemainingMilliseconds = 0, History = [] };
        engine.Observe(snapshot, Now().AddMinutes(1));
        Equal(0, engine.State.Tables[1].LastHistoryCount);
    }),
    ("Bet requires safe remaining time", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        snapshot = snapshot with { RemainingMilliseconds = 7_999 };
        False(engine.Observe(snapshot, Now()).OfType<BetRequestedEvent>().Any());
        snapshot = snapshot with { RemainingMilliseconds = 10_000 };
        True(engine.Observe(snapshot, Now().AddSeconds(1)).OfType<BetRequestedEvent>().Any());
    }),
    ("Custom follow strategy uses its streak and stakes", () =>
    {
        var strategy = new StrategySettings { StreakLength = 4, Direction = StrategyDirection.Follow, Stakes = [5, 15] };
        var (engine, snapshot) = Ready([1, 1, 1, 1], strategy);
        BetCandidate first = SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        Equal(BetSide.Banker, first.Side); Equal(5m, first.Amount);
        snapshot = snapshot with { GameSeq = 6, History = [.. snapshot.History, 2] };
        BetCandidate second = Candidate(engine.Observe(snapshot, Now().AddMinutes(1)));
        Equal(BetSide.Banker, second.Side); Equal(15m, second.Amount);
        SubmitAndAccept(engine, second);
        snapshot = snapshot with { GameSeq = 7, History = [.. snapshot.History, 1] };
        True(engine.Observe(snapshot, Now().AddMinutes(2)).OfType<ChaseCompletedEvent>().Single().Won);
    }),
    ("Custom trigger side filters the other side", () =>
    {
        var strategy = new StrategySettings { StreakLength = 3, TriggerSide = StrategyTriggerSide.BankerOnly, Stakes = [7] };
        var (playerEngine, playerSnapshot) = Ready([2, 2, 2], strategy);
        False(playerEngine.Observe(playerSnapshot, Now()).OfType<BetRequestedEvent>().Any());
        var (bankerEngine, bankerSnapshot) = Ready([1, 1, 1], strategy);
        Equal(7m, Candidate(bankerEngine.Observe(bankerSnapshot, Now())).Amount);
    }),
    ("Strategy change cannot discard an unsettled order", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        var changed = new StrategySettings { StreakLength = 4, Stakes = [10] };
        Throws<InvalidDataException>(() => new StrategyEngine(engine.State, changed));
    }),
    ("Custom strategy validation rejects unsafe values", () =>
    {
        Throws<ArgumentException>(() => new StrategySettings { StreakLength = 1 }.Validate());
        Throws<ArgumentException>(() => new StrategySettings { Stakes = [] }.Validate());
        Throws<ArgumentException>(() => new StrategySettings { Stakes = [10.5m] }.Validate());
        new StrategySettings { StreakLength = 20, Stakes = [1, 1_000_000] }.Validate();
    }),
    ("Legacy settings load the default strategy", () => WithStore(store =>
    {
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.SettingsPath, "{\"Version\":1,\"Mode\":\"ReadOnly\",\"AllowedBundle\":\"index.0e81c.js\"}");
        YaxinSettings settings = store.LoadSettings();
        Equal(FixedStrategy.Id, settings.Strategy.Id);
    })),
    ("Custom settings persist all strategy fields", () => WithStore(store =>
    {
        var expected = new StrategySettings
        {
            StreakLength = 8,
            TriggerSide = StrategyTriggerSide.PlayerOnly,
            Direction = StrategyDirection.Follow,
            Stakes = [12, 24, 48, 96]
        };
        store.SaveSettings(new YaxinSettings { Strategy = expected });
        string json = File.ReadAllText(store.SettingsPath);
        False(json.Contains("\"Id\"", StringComparison.Ordinal));
        False(json.Contains("\"Summary\"", StringComparison.Ordinal));
        StrategySettings actual = store.LoadSettings().Strategy;
        Equal(expected.Id, actual.Id);
        True(expected.Stakes.SequenceEqual(actual.Stakes));
    })),
    ("Invalid live limits are rejected", () =>
    {
        Throws<ArgumentException>(() => new YaxinSettings { Mode = MonitorMode.Live }.Validate());
        new YaxinSettings { Mode = MonitorMode.Live, DailyStakeLimit = 100, MaxReservedStake = 70 }.Validate();
    }),
    ("Persisted in-flight order becomes unknown", () => WithStore(store =>
    {
        var state = new EngineState();
        state.Tables[1] = new TableRuntimeState
        {
            TableId = 1, ShoeSeq = 1,
            ActiveChase = new ChaseTaskState { TaskKey = "t", PendingOrderKey = "o", Status = ChaseStatus.AwaitingSettlement }
        };
        state.Orders["o"] = new OrderState { OrderKey = "o", TableId = 1, Status = "Accepted" };
        store.SaveState(state);
        EngineState loaded = store.LoadState();
        Equal("Unknown", loaded.Orders["o"].Status);
        Equal(ChaseStatus.Unknown, loaded.Tables[1].ActiveChase!.Status);
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

static DateTimeOffset Now() => new(2026, 9, 20, 10, 0, 0, TimeSpan.FromHours(8));

static (StrategyEngine Engine, TableSnapshot Snapshot) Ready(int[] history, StrategySettings? strategy = null)
{
    var engine = strategy is null ? new StrategyEngine(new()) : new StrategyEngine(new(), strategy);
    return (engine, new TableSnapshot
    {
        TableId = 1, TableName = "B1", ShoeSeq = 1, GameSeq = history.Length + 1,
        State = "A", RemainingMilliseconds = 20_000, History = history,
        PlayerMin = 10, PlayerMax = 10_000, BankerMin = 10, BankerMax = 10_000
    });
}

static BetCandidate Candidate(IReadOnlyList<EngineEvent> events) => events.OfType<BetRequestedEvent>().Single().Candidate;

static BetCandidate SubmitAndAccept(StrategyEngine engine, BetCandidate candidate)
{
    engine.MarkSubmitted(candidate, Now());
    engine.MarkAccepted(candidate.OrderKey, Now());
    return candidate;
}

static void True(bool value) { if (!value) throw new Exception("Expected true."); }
static void False(bool value) => True(!value);
static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static void WithStore(Action<StateStore> action)
{
    string directory = Path.Combine(Path.GetTempPath(), "yaxin-monitor-test-" + Guid.NewGuid().ToString("N"));
    try { action(new StateStore(directory)); }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
