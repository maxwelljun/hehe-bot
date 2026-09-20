(async function () {
  "use strict";
  const existingBridge = window.__YAXIN_MONITOR__;

  const resourceUrls = Array.from(document.scripts, script => script.src || "");
  if (window.performance && typeof window.performance.getEntriesByType === "function")
    resourceUrls.push(...window.performance.getEntriesByType("resource").map(entry => entry.name || ""));
  const bundle = resourceUrls
    .find(src => /\/main\/index\.[a-z0-9]+\.js(?:\?|$)/i.test(src)) || "";
  const tableManager = window.TableManager;
  const gameManager = window.GameManager;
  if (!tableManager || !gameManager) return { ok: false, error: "页面游戏管理器尚未初始化。" };

  const stateNames = { 0: "RP", 1: "S", 2: "A", 3: "D", 20: "MC", 21: "MCP", 22: "MCB", 1000: "R" };
  const events = [];
  let sequence = 0;
  let pending = null;
  const hookedTables = new WeakSet();
  let existingInfo = null;
  try { existingInfo = existingBridge && existingBridge.info(); } catch (_) { }
  const sessionId = existingInfo && existingInfo.sessionId
    || (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random());

  function finite(value) {
    const number = Number(value);
    return Number.isFinite(number) ? number : null;
  }

  function emit(type, payload) {
    events.push(Object.assign({ type, sequence: ++sequence, sessionId }, payload || {}));
    if (events.length > 500) events.splice(0, events.length - 500);
  }

  function errorMessage(errorCode) {
    const languages = window.languages || {};
    const currentLanguage = window._languageData && window._languageData.language || "zh";
    const currentErrors = languages[currentLanguage] && languages[currentLanguage].ERROR || {};
    const zhErrors = languages.zh && languages.zh.ERROR || {};
    return String(currentErrors[String(errorCode)] || zhErrors[String(errorCode)] || "");
  }

  function tableSnapshot(ctrl) {
    const round = ctrl.tableRoundInfoBean;
    const state = ctrl.tableStateInfo;
    const road = ctrl.tableRoadBean;
    const limits = ctrl.tableBetBean && ctrl.tableBetBean.betZoneLimitData || {};
    const player = limits.PLAYER || {};
    const banker = limits.BANKER || {};
    return {
      tableId: Number(ctrl.tableId),
      tableName: String(ctrl.tableName || ctrl.tableNoName || ctrl.tableId),
      shoeSeq: Number(round && round.shoeSeq || 0),
      gameSeq: Number(round && round.gameSeq || 0),
      state: stateNames[state && state.tableState] || String(state && state.tableState || ""),
      remainingMilliseconds: Math.max(0, Math.floor(Number(state && state.stateCountdown || 0))),
      history: Array.from(road && road.history || [], value => Number(value)),
      playerMin: finite(player.minLimit),
      playerMax: finite(player.maxLimit),
      bankerMin: finite(banker.minLimit),
      bankerMax: finite(banker.maxLimit)
    };
  }

  function captureBetAck(message) {
    const tableId = Number(message && message.tableId || 0);
    const responseGameSeq = Number(message && message.gamblingNum || 0);
    const match = pending && pending.tableId === tableId
      && (responseGameSeq === 0 || pending.gameSeq === responseGameSeq) ? pending : null;
    if (!match) return;
    emit("betAck", {
      orderKey: match.orderKey,
      tableId,
      gameSeq: match.gameSeq,
      errorCode: Number(message && message.errorCode || 0),
      errorMessage: errorMessage(Number(message && message.errorCode || 0))
    });
    pending = null;
  }

  function hookTable(ctrl) {
    if (hookedTables.has(ctrl)) return true;
    const original = ctrl && ctrl.setBetResponseMsg;
    if (typeof original !== "function") return false;
    ctrl.setBetResponseMsg = function (message) {
      try {
        return original.apply(this, arguments);
      } finally {
        captureBetAck(message);
      }
    };
    hookedTables.add(ctrl);
    return true;
  }

  function allTables() {
    const map = tableManager._tableDataCtrlMap || {};
    return Object.keys(map).map(key => map[key]).filter(ctrl => ctrl && Number(ctrl.gameType) === 1)
      .map(ctrl => { hookTable(ctrl); return tableSnapshot(ctrl); });
  }

  const bridge = {
    info() {
      return { ok: true, bridgeVersion: 2, sessionId, bundle: bundle.split("/").pop().split("?")[0] };
    },
    poll() {
      const tables = allTables();
      const playerInfo = gameManager.PlayerInfo;
      const hasUserId = playerInfo && playerInfo.userId !== null && playerInfo.userId !== undefined
        && String(playerInfo.userId).length > 0;
      const loggedIn = Boolean(hasUserId || playerInfo && playerInfo.limitkey && tables.length > 0);
      const socketConnected = Boolean(gameManager.IsInSocket);
      return {
        ready: loggedIn && tables.length > 0 && socketConnected,
        loggedIn,
        socketConnected,
        sessionId,
        bundle: bundle.split("/").pop().split("?")[0],
        balance: finite(playerInfo && playerInfo.sumAmount) || 0,
        tables,
        events: events.splice(0, events.length)
      };
    },
    submitBet(request) {
      if (!request || pending) return { submitted: false, error: "已有订单等待网站确认。" };
      const orderKey = typeof request.orderKey === "string" ? request.orderKey : "";
      const tableId = Number(request.tableId);
      const shoeSeq = Number(request.shoeSeq);
      const gameSeq = Number(request.gameSeq);
      const minimumRemaining = Number(request.minimumRemainingMilliseconds);
      const amount = Number(request.amount);
      if (!orderKey || orderKey.length > 300) return { submitted: false, error: "订单键无效。" };
      if (!Number.isSafeInteger(tableId) || tableId <= 0 || !Number.isSafeInteger(shoeSeq) || shoeSeq < 0
          || !Number.isSafeInteger(gameSeq) || gameSeq < 0)
        return { submitted: false, error: "桌台、牌靴或局号无效。" };
      if (!Number.isFinite(minimumRemaining) || minimumRemaining < 1000 || minimumRemaining > 60000)
        return { submitted: false, error: "下注安全余量无效。" };
      if (!Number.isSafeInteger(amount) || amount <= 0 || amount > 1000000)
        return { submitted: false, error: "下注金额无效。" };
      const ctrl = (tableManager._tableDataCtrlMap || {})[String(tableId)];
      if (!ctrl || Number(ctrl.gameType) !== 1) return { submitted: false, error: "找不到百家乐桌台。" };
      if (!hookTable(ctrl)) return { submitted: false, error: "无法监听目标桌台的下注回执。" };
      if (!ctrl.tableRoundInfoBean || Number(ctrl.tableRoundInfoBean.shoeSeq) !== shoeSeq)
        return { submitted: false, error: "目标牌靴已变化。" };
      if (Number(ctrl.tableRoundInfoBean.gameSeq) !== gameSeq) return { submitted: false, error: "目标局号已变化。" };
      if (Number(ctrl.tableStateInfo.tableState) !== 2) return { submitted: false, error: "桌台不在下注状态。" };
      if (Number(ctrl.tableStateInfo.stateCountdown) < minimumRemaining)
        return { submitted: false, error: "剩余下注时间不足。" };
      if (request.side !== "PLAYER" && request.side !== "BANKER") return { submitted: false, error: "下注方向无效。" };
      const playerInfo = gameManager.PlayerInfo;
      if (!playerInfo || !playerInfo.limitkey) return { submitted: false, error: "当前限额配置无效。" };
      if (Number(playerInfo.sumAmount) < amount) return { submitted: false, error: "余额不足。" };
      const bean = ctrl.tableBetBean;
      const limit = bean.betZoneLimitData && bean.betZoneLimitData[request.side];
      if (!limit || amount < Number(limit.minLimit) || amount > Number(limit.maxLimit))
        return { submitted: false, error: "下注金额不符合目标桌台限额。" };
      if (Number(bean.unconfirmBetTotalNum) > 0) return { submitted: false, error: "桌台存在未确认筹码。" };
      if (!gameManager.IsInSocket) return { submitted: false, error: "网站连接不可用。" };

      let betData = bean.betZoneMap[request.side];
      if (!betData) betData = bean.betZoneMap[request.side] = { confirmBetNum: 0, unConfirmBetNum: 0, preRoundBetNum: 0, betTime: 0 };
      betData.unConfirmBetNum = amount;
      bean.lastBetDataKey = request.side;
      pending = { orderKey, tableId, gameSeq };
      try {
        ctrl.reqBetMessage(1);
        return { submitted: true, error: "" };
      } catch (error) {
        pending = null;
        betData.unConfirmBetNum = 0;
        return { submitted: false, error: String(error && error.message || error) };
      }
    }
  };

  if (existingBridge && (typeof existingBridge === "object" || typeof existingBridge === "function"))
    Object.assign(existingBridge, bridge);
  else
    Object.defineProperty(window, "__YAXIN_MONITOR__", { value: bridge, configurable: true, writable: false });
  emit("bridgeReady", { tableId: 0, gameSeq: 0, errorCode: 0 });
  return bridge.info();
})()
