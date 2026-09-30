using YaxinMonitor.Core;

var tests = new (string Name, Action Run)[]
{
    ("Latest six creates opposite 10 bet", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = Candidate(engine.Observe(snapshot, Now()));
        Equal(BetSide.Player, bet.Side); Equal(10m, bet.Amount); Equal(1, bet.Attempt);
    }),
    ("Pattern progress counts forming alternation hands", () =>
    {
        int P(int[] h, StrategyPattern p) => PatternDetector.Progress(PatternDetector.RecentRuns(h), p);
        Equal(4, P([1, 2, 3, 1, 2], StrategyPattern.SingleAlternation));
        Equal(1, P([1, 1, 2], StrategyPattern.SingleAlternation));
        Equal(5, P([1, 1, 2, 2, 1], StrategyPattern.DoubleAlternation));
        Equal(0, P([1, 1, 2, 2, 2], StrategyPattern.DoubleAlternation));
        Equal(8, P([1, 1, 1, 2, 2, 2, 1, 1], StrategyPattern.TripleAlternation));
        Equal(3, P([2, 1, 1, 1], StrategyPattern.Streak));
        Equal(9, PatternDetector.RequiredHands(StrategyPattern.TripleAlternation, 6));
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
    ("Pruning drops only old finished orders", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate first = Candidate(engine.Observe(snapshot, Now()));
        engine.MarkSubmitted(first, Now()); engine.MarkRejected(first.OrderKey, -1, Now());
        snapshot = snapshot with { GameSeq = 8, History = [.. snapshot.History, 3] };
        BetCandidate second = Candidate(engine.Observe(snapshot, Now().AddMinutes(1)));
        engine.MarkSubmitted(second, Now().AddMinutes(1)); engine.MarkUnknown(second.OrderKey, Now().AddMinutes(1));
        False(engine.PruneOrders(Now().AddHours(1), TimeSpan.FromHours(24)));
        True(engine.PruneOrders(Now().AddDays(2), TimeSpan.FromHours(24)));
        False(engine.State.Orders.ContainsKey(first.OrderKey));
        True(engine.State.Orders.ContainsKey(second.OrderKey));
    }),
    ("Site business day starts at noon Beijing time", () =>
    {
        var beijing = TimeSpan.FromHours(8);
        Equal(new DateOnly(2026, 9, 29), SiteCalendar.BusinessDate(new DateTimeOffset(2026, 9, 30, 11, 59, 0, beijing)));
        Equal(new DateOnly(2026, 9, 30), SiteCalendar.BusinessDate(new DateTimeOffset(2026, 9, 30, 12, 0, 0, beijing)));
        Equal(new DateOnly(2026, 9, 30), SiteCalendar.BusinessDate(new DateTimeOffset(2026, 9, 30, 4, 30, 0, TimeSpan.Zero)));
        Equal(new DateOnly(2026, 9, 28), SiteCalendar.WeekStart(new DateOnly(2026, 9, 30)));
        Equal(new DateOnly(2026, 9, 28), SiteCalendar.WeekStart(new DateOnly(2026, 10, 4)));
        Equal(new DateOnly(2026, 9, 28), SiteCalendar.WeekStart(new DateOnly(2026, 9, 28)));
        Equal("2026-9-1", SiteCalendar.Format(new DateOnly(2026, 9, 1)));
    }),
    ("Unknown order blocks automatic progress", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = Candidate(engine.Observe(snapshot, Now()));
        engine.MarkSubmitted(bet, Now()); engine.MarkUnknown(bet.OrderKey, Now());
        snapshot = snapshot with { GameSeq = 8, History = [.. snapshot.History, 1] };
        False(engine.Observe(snapshot, Now().AddMinutes(1)).OfType<BetRequestedEvent>().Any());
        Equal(bet.Amount, engine.ReservedStake);
        Equal(bet.Amount, engine.UncertainStake);
    }),
    ("Late acceptance recovers an unknown order", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = Candidate(engine.Observe(snapshot, Now()));
        engine.MarkSubmitted(bet, Now());
        engine.MarkUnknown(bet.OrderKey, Now().AddSeconds(8));
        engine.MarkAccepted(bet.OrderKey, Now().AddSeconds(9));
        Equal("Accepted", engine.State.Orders[bet.OrderKey].Status);
        True(engine.State.Orders[bet.OrderKey].CountedInDailyStake);
        Equal(0m, engine.UncertainStake);
        Equal(ChaseStatus.AwaitingSettlement, engine.State.Tables[1].ActiveChase!.Status);
    }),
    ("Late rejection recovers an unknown order", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = Candidate(engine.Observe(snapshot, Now()));
        engine.MarkSubmitted(bet, Now());
        engine.MarkUnknown(bet.OrderKey, Now().AddSeconds(8));
        engine.MarkRejected(bet.OrderKey, -96, Now().AddSeconds(9));
        Equal("Rejected", engine.State.Orders[bet.OrderKey].Status);
        Equal(ChaseStatus.Ready, engine.State.Tables[1].ActiveChase!.Status);
    }),
    ("New shoe resets old task", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        Candidate(engine.Observe(snapshot, Now()));
        snapshot = snapshot with { ShoeSeq = 2, GameSeq = 7, History = [2, 2, 2, 2, 2, 2] };
        Equal(BetSide.Banker, Candidate(engine.Observe(snapshot, Now().AddMinutes(1))).Side);
    }),
    ("New shoe defers an accepted order and releases the table", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        snapshot = snapshot with { ShoeSeq = 2, GameSeq = 1, State = "S", RemainingMilliseconds = 0, History = [] };
        True(engine.Observe(snapshot, Now().AddMinutes(1)).OfType<SettlementDeferredEvent>().Any());
        Equal("SettlementPending", engine.State.Orders[bet.OrderKey].Status);
        Equal(bet.Amount, engine.ReservedStake);
        True(engine.State.Tables[1].ActiveChase is null);
    }),
    ("Site result push settles a loss before the road updates", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        IReadOnlyList<EngineEvent> events = engine.ApplySiteResult(1, "B1", 1, bet.GameSeq, BaccaratOutcome.Banker, Now());
        Equal(BaccaratOutcome.Banker, events.OfType<SettlementEvent>().Single().Outcome);
        Equal("Settled", engine.State.Orders[bet.OrderKey].Status);
        Equal(1, engine.State.Tables[1].ActiveChase!.AttemptIndex);
        snapshot = snapshot with { GameSeq = 8, History = [.. snapshot.History, 1] };
        events = engine.Observe(snapshot, Now().AddMinutes(1));
        False(events.OfType<SettlementEvent>().Any());
        Equal(20m, Candidate(events).Amount);
    }),
    ("Site result push wins and ignores repeats", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        True(engine.ApplySiteResult(1, "B1", 1, bet.GameSeq, BaccaratOutcome.Player, Now()).OfType<ChaseCompletedEvent>().Single().Won);
        Equal(0, engine.ApplySiteResult(1, "B1", 1, bet.GameSeq, BaccaratOutcome.Player, Now()).Count);
        Equal(0, engine.ApplySiteResult(1, "B1", 1, bet.GameSeq + 1, BaccaratOutcome.Player, Now()).Count);
    }),
    ("Site result push settles an order deferred by a new shoe", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        engine.Observe(snapshot with { ShoeSeq = 2, GameSeq = 1, State = "S", History = [] }, Now().AddMinutes(1));
        Equal("SettlementPending", engine.State.Orders[bet.OrderKey].Status);
        engine.ApplySiteResult(1, "B1", 1, bet.GameSeq, BaccaratOutcome.Banker, Now().AddMinutes(1));
        Equal("Settled", engine.State.Orders[bet.OrderKey].Status);
        Equal(0m, engine.ReservedStake);
    }),
    ("Site result push confirms an order whose ack timed out", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = Candidate(engine.Observe(snapshot, Now()));
        engine.MarkSubmitted(bet, Now());
        engine.MarkUnknown(bet.OrderKey, Now());
        Equal(10m, engine.UncertainStake);
        engine.ApplySiteResult(1, "B1", 1, bet.GameSeq, BaccaratOutcome.Tie, Now());
        Equal("Settled", engine.State.Orders[bet.OrderKey].Status);
        Equal(10m, engine.State.DailyAcceptedStake);
        Equal(0m, engine.UncertainStake);
        Equal(ChaseStatus.Ready, engine.State.Tables[1].ActiveChase!.Status);
    }),
    ("New shoe may create a new order while old settlement is pending", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate old = SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        snapshot = snapshot with { ShoeSeq = 2, GameSeq = 7, History = [2, 2, 2, 2, 2, 2] };
        IReadOnlyList<EngineEvent> events = engine.Observe(snapshot, Now().AddMinutes(1));
        Equal("SettlementPending", engine.State.Orders[old.OrderKey].Status);
        Equal(BetSide.Banker, Candidate(events).Side);
    }),
    ("New shoe still blocks an order with unknown acceptance", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = Candidate(engine.Observe(snapshot, Now()));
        engine.MarkSubmitted(bet, Now());
        engine.MarkUnknown(bet.OrderKey, Now());
        snapshot = snapshot with { ShoeSeq = 2, GameSeq = 1, State = "S", RemainingMilliseconds = 0, History = [] };
        Throws<InvalidDataException>(() => engine.Observe(snapshot, Now().AddMinutes(1)));
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
    ("Strategy change cannot discard an unknown order", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = Candidate(engine.Observe(snapshot, Now()));
        engine.MarkSubmitted(bet, Now());
        engine.MarkUnknown(bet.OrderKey, Now());
        var changed = new StrategySettings { StreakLength = 4, Stakes = [10] };
        Throws<InvalidDataException>(() => new StrategyEngine(engine.State, changed));
    }),
    ("Manual reconciliation allows a strategy change", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = Candidate(engine.Observe(snapshot, Now()));
        engine.MarkSubmitted(bet, Now());
        engine.MarkUnknown(bet.OrderKey, Now());
        engine.ResolveOrder(bet.OrderKey, ManualOrderResolution.ConfirmedNotPlaced, Now().AddMinutes(1));
        Equal("ManuallyConfirmedNotPlaced", engine.State.Orders[bet.OrderKey].Status);
        True(engine.State.Tables[1].ActiveChase is null);
        var changed = new StrategySettings { StreakLength = 4, Stakes = [10] };
        _ = new StrategyEngine(engine.State, changed);
    }),
    ("Manual reconciliation rejects a known pending order", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        Throws<InvalidOperationException>(() => engine.ResolveOrder(
            bet.OrderKey, ManualOrderResolution.ConfirmedSettled, Now().AddMinutes(1)));
        Equal(ChaseStatus.AwaitingSettlement, engine.State.Tables[1].ActiveChase!.Status);
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
    ("Persisted accepted order becomes pending settlement and releases table", () => WithStore(store =>
    {
        var state = new EngineState();
        state.Tables[1] = new TableRuntimeState
        {
            TableId = 1, ShoeSeq = 1,
            ActiveChase = new ChaseTaskState
            {
                TaskKey = "t", PendingOrderKey = "o", Status = ChaseStatus.AwaitingSettlement,
                StreakSide = BaccaratOutcome.Banker, BetSide = BetSide.Player
            }
        };
        state.Orders["o"] = new OrderState { OrderKey = "o", TableId = 1, Amount = 100, Status = "Accepted" };
        state.DailyAcceptedStake = 100;
        store.SaveState(state);
        EngineState loaded = store.LoadState();
        Equal("SettlementPending", loaded.Orders["o"].Status);
        True(loaded.Orders["o"].CountedInDailyStake);
        Equal(0m, new StrategyEngine(loaded).UncertainStake);
        Equal(100m, new StrategyEngine(loaded).ReservedStake);
        True(loaded.Tables[1].ActiveChase is null);
    })),
    ("Persisted submitted order remains unknown and blocks table", () => WithStore(store =>
    {
        var state = new EngineState();
        state.Tables[1] = new TableRuntimeState
        {
            TableId = 1, ShoeSeq = 1,
            ActiveChase = new ChaseTaskState
            {
                TaskKey = "t", PendingOrderKey = "o", Status = ChaseStatus.AwaitingAcceptance,
                StreakSide = BaccaratOutcome.Banker, BetSide = BetSide.Player
            }
        };
        state.Orders["o"] = new OrderState { OrderKey = "o", TableId = 1, Amount = 100, Status = "Submitted" };
        store.SaveState(state);
        EngineState loaded = store.LoadState();
        Equal("Unknown", loaded.Orders["o"].Status);
        Equal(ChaseStatus.Unknown, loaded.Tables[1].ActiveChase!.Status);
    })),
    ("Legacy counted unknown order migrates to pending settlement", () => WithStore(store =>
    {
        var state = new EngineState();
        state.Tables[1] = new TableRuntimeState
        {
            TableId = 1, ShoeSeq = 1,
            ActiveChase = new ChaseTaskState
            {
                TaskKey = "t", PendingOrderKey = "o", Status = ChaseStatus.Unknown,
                StreakSide = BaccaratOutcome.Banker, BetSide = BetSide.Player
            }
        };
        state.Orders["o"] = new OrderState
        {
            OrderKey = "o", TableId = 1, Amount = 100, Status = "Unknown", CountedInDailyStake = true
        };
        store.SaveState(state);
        EngineState loaded = store.LoadState();
        Equal("SettlementPending", loaded.Orders["o"].Status);
        True(loaded.Tables[1].ActiveChase is null);
    })),
    ("Manual settlement reconciliation releases reserved stake", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1]);
        BetCandidate bet = SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        snapshot = snapshot with { ShoeSeq = 2, GameSeq = 1, State = "S", RemainingMilliseconds = 0, History = [] };
        engine.Observe(snapshot, Now().AddMinutes(1));
        Throws<InvalidOperationException>(() => engine.ResolveOrder(
            bet.OrderKey, ManualOrderResolution.ConfirmedNotPlaced, Now().AddMinutes(2)));
        engine.ResolveOrder(bet.OrderKey, ManualOrderResolution.ConfirmedSettled, Now().AddMinutes(2));
        Equal("ManuallyConfirmedSettled", engine.State.Orders[bet.OrderKey].Status);
        Equal(0m, engine.ReservedStake);
    }),
    ("Alternation patterns bet opposite of the last result", () =>
    {
        foreach (var (pattern, history, side) in new (StrategyPattern, int[], BetSide)[]
        {
            (StrategyPattern.SingleAlternation, [1, 2, 1, 2, 1, 2], BetSide.Banker),
            (StrategyPattern.DoubleAlternation, [1, 1, 2, 2, 1, 1], BetSide.Player),
            (StrategyPattern.TripleAlternation, [1, 1, 1, 2, 2, 2, 1, 1, 1], BetSide.Player),
            (StrategyPattern.SingleAlternation, [2, 1, 2, 1, 2, 1], BetSide.Player),
            (StrategyPattern.DoubleAlternation, [2, 2, 1, 1, 2, 2], BetSide.Banker),
            (StrategyPattern.TripleAlternation, [2, 2, 2, 1, 1, 1, 2, 2, 2], BetSide.Banker)
        })
        {
            var (engine, snapshot) = Ready(history, Patterns(pattern));
            BetCandidate bet = Candidate(engine.Observe(snapshot, Now()));
            Equal(side, bet.Side); Equal(10m, bet.Amount);
            Equal(pattern, engine.State.Tables[1].ActiveChase!.Pattern);
            var (shorter, shortSnapshot) = Ready(history[..^1], Patterns(pattern));
            False(shorter.Observe(shortSnapshot, Now()).OfType<BetRequestedEvent>().Any());
        }
    }),
    ("Alternation runs must match exactly", () =>
    {
        foreach (var (pattern, history) in new (StrategyPattern, int[])[]
        {
            (StrategyPattern.DoubleAlternation, [1, 1, 1, 2, 2, 1, 1]),
            (StrategyPattern.DoubleAlternation, [1, 1, 2, 2, 1, 1, 1]),
            (StrategyPattern.TripleAlternation, [1, 1, 2, 2, 2, 1, 1, 1]),
            (StrategyPattern.SingleAlternation, [1, 1, 2, 1, 2, 1]),
            (StrategyPattern.Streak, [1, 2, 1, 2, 1, 2])
        })
        {
            var (engine, snapshot) = Ready(history, Patterns(pattern));
            False(engine.Observe(snapshot, Now()).OfType<BetRequestedEvent>().Any());
        }
    }),
    ("Ties are ignored inside alternation", () =>
    {
        var (engine, snapshot) = Ready([1, 3, 2, 1, 3, 3, 2, 1, 2], Patterns(StrategyPattern.SingleAlternation));
        Equal(BetSide.Banker, Candidate(engine.Observe(snapshot, Now())).Side);
    }),
    ("Alternation loss advances stakes on the same side", () =>
    {
        var (engine, snapshot) = Ready([1, 1, 2, 2, 1, 1], Patterns(StrategyPattern.DoubleAlternation));
        SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        snapshot = snapshot with { GameSeq = 8, History = [.. snapshot.History, 1] };
        BetCandidate second = Candidate(engine.Observe(snapshot, Now().AddMinutes(1)));
        Equal(BetSide.Player, second.Side); Equal(20m, second.Amount); Equal(2, second.Attempt);
    }),
    ("Extended alternation triggers only once", () =>
    {
        var (engine, snapshot) = Ready([1, 2, 1, 2, 1, 2], Patterns(StrategyPattern.SingleAlternation));
        SubmitAndAccept(engine, Candidate(engine.Observe(snapshot, Now())));
        snapshot = snapshot with { GameSeq = 8, History = [.. snapshot.History, 1] };
        var events = engine.Observe(snapshot, Now().AddMinutes(1));
        True(events.OfType<ChaseCompletedEvent>().Single().Won);
        False(events.OfType<SignalEvent>().Any());
        snapshot = snapshot with { GameSeq = 9, History = [.. snapshot.History, 2] };
        False(engine.Observe(snapshot, Now().AddMinutes(2)).OfType<SignalEvent>().Any());
    }),
    ("Multiple patterns share one chase per table", () =>
    {
        var strategy = new StrategySettings
        {
            StreakLength = 2,
            Patterns = [StrategyPattern.DoubleAlternation, StrategyPattern.Streak]
        };
        var (engine, snapshot) = Ready([1, 1, 2, 2, 1, 1], strategy);
        var events = engine.Observe(snapshot, Now());
        Equal(1, events.OfType<SignalEvent>().Count());
        Equal(BetSide.Player, Candidate(events).Side);
        Equal(StrategyPattern.Streak, engine.State.Tables[1].ActiveChase!.Pattern);
    }),
    ("Pattern seen during an active chase is consumed", () =>
    {
        var strategy = Patterns(StrategyPattern.Streak, StrategyPattern.SingleAlternation);
        var (engine, snapshot) = Ready([1, 1, 1, 1, 1, 1], strategy);
        snapshot = snapshot with { State = "D" };
        engine.Observe(snapshot, Now());
        snapshot = snapshot with { History = [.. snapshot.History, 2, 1, 2, 1, 2, 1] };
        False(engine.Observe(snapshot, Now()).OfType<SignalEvent>().Any());
        Equal(StrategyPattern.Streak, engine.State.Tables[1].ActiveChase!.Pattern);
        engine.State.Tables[1].ActiveChase = null;
        engine.State.Tables[1].LastHistoryCount = 0;
        False(engine.Observe(snapshot with { State = "A" }, Now()).OfType<SignalEvent>().Any());
    }),
    ("Pattern selection changes strategy id and validates", () =>
    {
        Equal(FixedStrategy.Id, new StrategySettings().Id);
        Equal("custom-v1-6-0-0-10_20_40-p0_2", Patterns(StrategyPattern.DoubleAlternation, StrategyPattern.Streak).Id);
        Throws<ArgumentException>(() => Patterns().Validate());
        Throws<ArgumentException>(() => Patterns(StrategyPattern.Streak, StrategyPattern.Streak).Validate());
    }),
    ("Legacy settings without patterns default to streak", () => WithStore(store =>
    {
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.SettingsPath, "{\"Version\":1,\"Strategy\":{\"StreakLength\":6}}");
        StrategySettings loaded = store.LoadSettings().Strategy;
        True(loaded.Patterns is [StrategyPattern.Streak]);
        store.SaveSettings(new YaxinSettings { Strategy = Patterns(StrategyPattern.TripleAlternation) });
        True(store.LoadSettings().Strategy.Patterns is [StrategyPattern.TripleAlternation]);
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

static StrategySettings Patterns(params StrategyPattern[] patterns) => new() { Patterns = patterns };

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
