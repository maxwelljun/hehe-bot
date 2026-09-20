"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

async function main() {
  let submitCount = 0;
  let originalAckCount = 0;
  const ctrl = {
    tableId: 18,
    tableName: "Baccarat 18",
    gameType: 1,
    tableRoundInfoBean: { shoeSeq: 7, gameSeq: 42 },
    tableStateInfo: { tableState: 2, stateCountdown: 15000 },
    tableRoadBean: { history: [1, 1, 1, 1, 1, 1] },
    tableBetBean: {
      betZoneLimitData: {
        PLAYER: { minLimit: 10, maxLimit: 1000 },
        BANKER: { minLimit: 10, maxLimit: 1000 }
      },
      betZoneMap: {},
      unconfirmBetTotalNum: 0
    },
    reqBetMessage(type) {
      assert.equal(type, 1);
      submitCount++;
    },
    setBetResponseMsg() {
      originalAckCount++;
    }
  };

  const context = {
    window: {
      TableManager: { _tableDataCtrlMap: { "18": ctrl } },
      GameManager: {
        PlayerInfo: { userId: 123, sumAmount: 5000, limitkey: "limit-a" },
        IsInSocket: true
      },
      performance: { getEntriesByType: () => [] }
    },
    document: { scripts: [{ src: "https://example.test/main/index.0e81c.js" }] },
    crypto: { randomUUID: () => "test-session" },
    Number,
    String,
    Boolean,
    Array,
    Object,
    Math,
    Date,
    WeakSet
  };
  context.window.languages = { zh: { ERROR: { "-96": "测试拒绝原因" } } };
  context.window._languageData = { language: "zh" };
  context.window.performance = context.window.performance;

  const bridgePath = path.join(__dirname, "..", "src", "YaxinMonitor.Windows", "SiteBridge.js");
  const source = fs.readFileSync(bridgePath, "utf8");
  assert.doesNotMatch(source, /System\s*\.\s*import/);

  const info = await vm.runInNewContext(source, context, { filename: bridgePath });
  assert.deepEqual(JSON.parse(JSON.stringify(info)), {
    ok: true,
    bridgeVersion: 2,
    sessionId: "test-session",
    bundle: "index.0e81c.js"
  });

  const firstPoll = context.window.__YAXIN_MONITOR__.poll();
  assert.equal(firstPoll.ready, true);
  assert.equal(firstPoll.loggedIn, true);
  assert.equal(firstPoll.socketConnected, true);
  assert.equal(firstPoll.balance, 5000);
  assert.equal(firstPoll.tables.length, 1);
  assert.deepEqual(Array.from(firstPoll.tables[0].history), [1, 1, 1, 1, 1, 1]);

  const request = {
    orderKey: "strategy:18:7:42:1",
    tableId: 18,
    shoeSeq: 7,
    gameSeq: 42,
    minimumRemainingMilliseconds: 8000,
    amount: 10,
    side: "PLAYER"
  };
  assert.deepEqual(JSON.parse(JSON.stringify(context.window.__YAXIN_MONITOR__.submitBet(request))), {
    submitted: true,
    error: ""
  });
  assert.equal(submitCount, 1);
  assert.equal(ctrl.tableBetBean.betZoneMap.PLAYER.unConfirmBetNum, 10);

  ctrl.setBetResponseMsg({ tableId: 99, gamblingNum: 42, errorCode: 0 });
  assert.equal(context.window.__YAXIN_MONITOR__.poll().events.length, 0);
  ctrl.setBetResponseMsg({ tableId: 18, gamblingNum: 41, errorCode: 0 });
  assert.equal(context.window.__YAXIN_MONITOR__.poll().events.length, 0);
  ctrl.setBetResponseMsg({ tableId: 18, gamblingNum: 42, errorCode: 0 });
  assert.equal(originalAckCount, 3);

  const ackEvents = context.window.__YAXIN_MONITOR__.poll().events;
  assert.equal(ackEvents.length, 1);
  assert.equal(ackEvents[0].type, "betAck");
  assert.equal(ackEvents[0].orderKey, request.orderKey);
  assert.equal(ackEvents[0].tableId, 18);
  assert.equal(ackEvents[0].gameSeq, 42);
  assert.equal(ackEvents[0].errorCode, 0);
  assert.equal(ackEvents[0].errorMessage, "");

  ctrl.tableRoundInfoBean.gameSeq = 43;
  const rejectedRequest = { ...request, orderKey: "strategy:18:7:43:1", gameSeq: 43 };
  assert.equal(context.window.__YAXIN_MONITOR__.submitBet(rejectedRequest).submitted, true);
  ctrl.setBetResponseMsg({ tableId: 18, gamblingNum: 43, errorCode: -96 });
  const rejectedEvents = context.window.__YAXIN_MONITOR__.poll().events;
  assert.equal(rejectedEvents.length, 1);
  assert.equal(rejectedEvents[0].errorCode, -96);
  assert.equal(rejectedEvents[0].errorMessage, "测试拒绝原因");

  context.window.GameManager.PlayerInfo.userId = null;
  assert.equal(context.window.__YAXIN_MONITOR__.poll().loggedIn, true);
  context.window.GameManager.IsInSocket = false;
  const disconnectedPoll = context.window.__YAXIN_MONITOR__.poll();
  assert.equal(disconnectedPoll.ready, false);
  assert.equal(disconnectedPoll.socketConnected, false);

  const secondInfo = await vm.runInNewContext(source, context, { filename: bridgePath });
  assert.equal(secondInfo.bridgeVersion, 2);
  assert.equal(secondInfo.sessionId, "test-session");
  console.log("SiteBridge tests passed.");
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
