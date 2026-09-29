"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const {test} = require("node:test");

const html = fs.readFileSync(path.join(__dirname, "../tools/offline-replay/index.html"), "utf8");
const source = html.match(/<script id="replay-engine">([\s\S]*?)<\/script>/)[1];
const engine = vm.runInNewContext(source + "\nOfflineReplay;");
const defaults = {modes: ["streak", "single", "double", "triple"], streakLength: 6, stakes: [10, 20, 40]};
const replay = (history, modes, extra = {}) => engine.replay(`A | 1 | ${history}`, {...defaults, modes, ...extra});
const plain = value => JSON.parse(JSON.stringify(value));

for (const [mode, history, trigger, side] of [
  ["streak", "BBBBBB", 6, "P"], ["single", "BPBPBP", 6, "B"],
  ["double", "BBPPBB", 6, "P"], ["triple", "BBBPPPBBB", 9, "P"]
]) {
  test(`${mode}: exact trigger, opposite side, no future data`, () => {
    const result = replay(history, [mode]);
    assert.equal(result.tasks.length, 1);
    assert.equal(result.tasks[0].trigger, trigger);
    assert.equal(result.tasks[0].side, side);
    assert.equal(result.tasks[0].status, "pending");
    assert.equal(result.rows[0].round, null);
    assert.equal(result.summaries[0].net, 0);
    assert.equal(replay(history.slice(0, -1), [mode]).tasks.length, 0);
  });
  test(`${mode}: mirrored pattern supported`, () => {
    const mirror = history.replace(/[BP]/g, value => value === "B" ? "P" : "B");
    assert.equal(replay(mirror, [mode]).tasks[0].side, side === "B" ? "P" : "B");
  });
}

test("First hit ends task after one trial", () => {
  const result = replay("BPBPBPBB", ["single"]);
  assert.equal(result.tasks[0].status, "hit");
  assert.equal(result.rows.length, 1);
  assert.equal(result.rows[0].round, 7);
  assert.equal(result.summaries[0].net, 10);
});

test("Keep initial side for all three trials and stop on third hit", () => {
  const result = replay("BPBPBPPPBP", ["single"]);
  assert.deepEqual(plain(result.rows.map(row => [row.side, row.attempt, row.amount, row.result])),
    [["B", 1, 10, "miss"], ["B", 2, 20, "miss"], ["B", 3, 40, "hit"]]);
  assert.equal(result.summaries[0].net, 10);
  assert.equal(result.tasks[0].status, "hit");
});

test("Three misses end task without fourth trial", () => {
  const result = replay("BBBPPPBBBBBBB", ["triple"]);
  assert.equal(result.rows.length, 3);
  assert.ok(result.rows.every(row => row.side === "P" && row.result === "miss"));
  assert.equal(result.tasks[0].status, "miss");
  assert.equal(result.summaries[0].net, -70);
});

test("Second hit ends task and configured points are used", () => {
  const result = replay("BBPPBBBPB", ["double"], {stakes: [50, 100, 200]});
  assert.deepEqual(plain(result.rows.map(row => row.amount)), [50, 100]);
  assert.equal(result.summaries[0].net, 50);
});

test("Ties ignored for recognition, no trailing-tie trigger or tier consumption", () => {
  const result = replay("BTPTBPTBPTTP", ["single"]);
  assert.equal(result.tasks.length, 1);
  assert.equal(result.tasks[0].trigger, 9);
  assert.deepEqual(plain(result.rows.map(row => [row.result, row.attempt])),
    [["tie", 1], ["tie", 1], ["miss", 1], ["pending", 2]]);
  assert.equal(result.summaries[0].pending, 1);
  assert.equal(result.summaries[0].net, -10);
});

test("Continuous alternating chains never retrigger after completion", () => {
  for (const [mode, history] of [["single", "BP".repeat(15)], ["double", "BBPP".repeat(8)], ["triple", "BBBPPP".repeat(8)]]) {
    assert.equal(replay(history, [mode]).tasks.length, 1, mode);
  }
  assert.equal(replay("B".repeat(40), ["streak"]).tasks.length, 1);
});

test("Broken pattern may later start a new chain", () => {
  const result = replay("BPBPBPBBBPBPBPB", ["single"]);
  assert.equal(result.tasks.length, 2);
  assert.equal(result.tasks[1].trigger, 15);
});

test("Runs are exact blocks, never a shifted suffix of a longer run", () => {
  assert.equal(replay("BBBPPBB", ["double"]).tasks.length, 0);
  assert.equal(replay("BBBBPPPBBB", ["triple"]).tasks.length, 0);
  assert.equal(replay("BBPBPBP", ["single"]).tasks.length, 0);
});

test("Continuing two-run pattern is not queued after task completion", () => {
  const result = replay("BBPPBBPPBB", ["double"]);
  assert.equal(result.tasks.length, 1);
  assert.equal(result.tasks[0].status, "hit");
});

test("Each selected mode has independent records and counts", () => {
  const history = "BBBBBBPBPBPBBPPBBPBBBPPPBBBP";
  const together = replay(history, defaults.modes);
  for (const mode of defaults.modes) {
    const alone = replay(history, [mode]);
    assert.deepEqual(plain(together.tasks.filter(task => task.mode === mode)), plain(alone.tasks));
    assert.deepEqual(plain(together.summaries.find(item => item.mode === mode)), plain(alone.summaries[0]));
  }
});

test("Table and shoe boundaries isolate pending tasks", () => {
  const result = engine.replay("A|1|BBBBBB\nA|2|P\nB|1|BBBBBBP", {...defaults, modes: ["streak"]});
  assert.equal(result.tasks.length, 2);
  assert.equal(result.summaries[0].pending, 1);
  assert.equal(result.summaries[0].hit, 1);
  assert.equal(result.summaries[0].net, 10);
});

test("Chinese input, BOM, separators, all ties, and custom streak", () => {
  const result = engine.replay("\uFEFFA | 1 | 庄，庄 庄、庄 和 闲", {...defaults, modes: ["streak"], streakLength: 4});
  assert.equal(result.tasks[0].status, "hit");
  assert.equal(result.rows[0].result, "tie");
  assert.equal(replay("TTTT", defaults.modes).tasks.length, 0);
});

test("Replay determinism and prefix stability prevent lookahead", () => {
  const text = "BPBPBPPPBBPBPBP";
  const full = replay(text, ["single"]);
  assert.deepEqual(plain(full), plain(replay(text, ["single"])));
  for (let end = 1; end <= text.length; end++) {
    const prefix = replay(text.slice(0, end), ["single"]);
    assert.deepEqual(plain(prefix.rows.filter(row => row.round !== null)), plain(full.rows.filter(row => row.round !== null && row.round <= end)));
  }
});

test("Invalid modes, tiers, histories and duplicate shoes fail clearly", () => {
  for (const extra of [{modes: []}, {modes: ["wat"]}, {modes: ["single", "single"]}, {stakes: []},
    {stakes: [10, 20, 40, 80]}, {stakes: [0]}, {stakes: [NaN]}, {stakes: [1.5]}, {streakLength: 1}]) {
    assert.throws(() => replay("BPBPBP", ["single"], extra));
  }
  for (const text of ["", "A|1|X", "A|1|", "A|1|B\nA|1|P", "missing separators", `A|1|${"B".repeat(50001)}`]) {
    assert.throws(() => engine.replay(text, defaults));
  }
});

test("CSV protects spreadsheet formulas and quotes labels", () => {
  const result = engine.replay('=1+1|"shoe"|BBBBBBB', {...defaults, modes: ["streak"]});
  const csv = engine.csv(result);
  assert.ok(csv.startsWith("\uFEFF"));
  assert.ok(csv.includes('"\'=1+1"'));
  assert.ok(csv.includes('"""shoe"""'));
  assert.ok(csv.includes('"-10"'));
});

test("50,000 alternating results stay bounded and deduplicated", () => {
  const result = replay("BP".repeat(25000), defaults.modes);
  assert.equal(result.tasks.length, 1);
  assert.equal(result.rows.length, 1);
});

test("Standalone page blocks network access and contains no remote dependencies", () => {
  assert.match(html, /connect-src 'none'/);
  assert.doesNotMatch(html, /<script[^>]+src=|<link[^>]+href=|\bfetch\s*\(|\bWebSocket\s*\(/);
  for (const match of html.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/g)) new vm.Script(match[1]);
});
