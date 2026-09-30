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
  const pendingOrders = [];
  const hookedTables = new WeakSet();
  const hookedResultTables = new WeakSet();
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
    const matchIndex = pendingOrders.findIndex(order => order.tableId === tableId
      && (responseGameSeq === 0 || order.gameSeq === responseGameSeq));
    if (matchIndex < 0) return;
    const match = pendingOrders[matchIndex];
    emit("betAck", {
      orderKey: match.orderKey,
      tableId,
      gameSeq: match.gameSeq,
      errorCode: Number(message && message.errorCode || 0),
      errorMessage: errorMessage(Number(message && message.errorCode || 0))
    });
    pendingOrders.splice(matchIndex, 1);
  }

  // The site pushes setBetResultMsgBC only for tables the player has chips on, right when the round is
  // drawn and before the road history updates, so it settles our own order even if the shoe ends.
  function betResultOutcome(message) {
    const result = String(message && message.result || "").toUpperCase();
    if (result === "BANKER" || result === "PLAYER" || result === "TIE") return result;
    const options = Array.isArray(message && message.winOption) ? message.winOption.map(String) : [];
    return options.includes("TIE") ? "TIE" : "";
  }

  function captureBetResult(ctrl, message) {
    const tableId = Number(ctrl.tableId);
    if (message && message.tableId != null && Number(message.tableId) !== tableId) return;
    const round = ctrl.tableRoundInfoBean || {};
    let shoeSeq = Number(round.shoeSeq || 0);
    let gameSeq = Number(round.gameSeq || 0);
    // videoId is tableId + yyyymmdd + two-digit shoe + two-digit game, e.g. 3008202609290509.
    const videoId = String(message && message.videoId || "");
    const prefix = String(tableId);
    if (videoId.startsWith(prefix) && videoId.length === prefix.length + 12 && /^\d+$/.test(videoId)) {
      shoeSeq = Number(videoId.slice(-4, -2));
      gameSeq = Number(videoId.slice(-2));
    }
    emit("betResult", {
      tableId,
      shoeSeq,
      gameSeq,
      errorCode: 0,
      result: betResultOutcome(message),
      winAmount: finite(message && message.winAmount)
    });
  }

  function hookTable(ctrl) {
    const originalResult = ctrl && ctrl.setBetResultMsgBC;
    if (!hookedResultTables.has(ctrl) && typeof originalResult === "function") {
      ctrl.setBetResultMsgBC = function (message) {
        try {
          return originalResult.apply(this, arguments);
        } finally {
          try { captureBetResult(ctrl, message); } catch (_) { }
        }
      };
      hookedResultTables.add(ctrl);
    }
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

  function allTableControllers() {
    const map = tableManager._tableDataCtrlMap || {};
    return Object.keys(map).map(key => map[key]).filter(ctrl => ctrl && Number(ctrl.gameType) === 1);
  }

  function inspectCompatibility(controllers) {
    const observationIssues = [];
    const bettingIssues = [];
    for (const ctrl of controllers) {
      const table = String(ctrl.tableId || "未知");
      if (!ctrl.tableRoundInfoBean || !ctrl.tableStateInfo || !ctrl.tableRoadBean
          || !ctrl.tableRoadBean.history || typeof ctrl.tableRoadBean.history[Symbol.iterator] !== "function")
        observationIssues.push(`桌台 ${table} 缺少局号、状态或路单接口`);
      const bean = ctrl.tableBetBean;
      const limits = bean && bean.betZoneLimitData;
      if (!bean || !bean.betZoneMap || !limits || !limits.PLAYER || !limits.BANKER
          || typeof ctrl.reqBetMessage !== "function" || typeof ctrl.setBetResponseMsg !== "function")
        bettingIssues.push(`桌台 ${table} 缺少下注、限额或回执接口`);
    }
    if (controllers.length === 0) observationIssues.push("没有百家乐桌台");
    const observationCompatible = observationIssues.length === 0;
    const bettingCompatible = observationCompatible && bettingIssues.length === 0;
    return {
      bridgeVersion: 3,
      observationCompatible,
      bettingCompatible,
      compatibilityError: observationIssues.concat(bettingIssues).slice(0, 5).join("；")
    };
  }

  // 网站投注记录接口（与网页"投注记录"同源），只取汇总：码量、洗码量、输赢。
  // 接口限制调用频率，多个区间之间间隔几秒串行查询。
  const turnover = { today: null, week: null, error: "" };
  let turnoverBusy = false;

  function sumAttribute(xml, name) {
    const match = new RegExp(name + '="([^"]*)"').exec(xml);
    return match ? finite(match[1]) : null;
  }

  async function fetchTurnover(range) {
    const playerInfo = gameManager.PlayerInfo;
    if (!playerInfo || !playerInfo.userId) throw new Error("网站尚未登录");
    let systemInfo = null;
    try { systemInfo = window.System.get("chunks:///_virtual/MCenter.ts").MCenter.systemInfo; } catch (_) { }
    const host = systemInfo && systemInfo.HTTP_HOST || location.origin;
    const query = new URLSearchParams({
      userId: String(playerInfo.userId), name: String(playerInfo.platformName || ""), pageIndex: "1", pageSize: "1",
      startTime: range.start, endTime: range.end, t: String(Date.now()), queryType: "1"
    });
    const headers = systemInfo && systemInfo.ApiToken ? { apiUserToken: systemInfo.ApiToken } : {};
    const response = await fetch(host + "/client/ClientbettingInfo.jsp?" + query, { headers, cache: "no-store" });
    const text = await response.text();
    if (!/<success>0<\/success>/.test(text)) {
      let message = text.slice(0, 120);
      try { message = JSON.parse(text).message || message; } catch (_) { }
      throw new Error("投注记录查询失败：" + message);
    }
    const count = /<total>(\d+)<\/total>/.exec(text);
    return {
      start: range.start, end: range.end, count: count ? Number(count[1]) : 0,
      bet: sumAttribute(text, "SUM_BETAMOUNT") || 0, valid: sumAttribute(text, "SUM_COMMAMOUNT") || 0,
      winLost: sumAttribute(text, "SUM_WINLOST") || 0, updatedAt: Date.now()
    };
  }

  async function refreshTurnover(ranges) {
    try {
      for (let i = 0; i < ranges.length; i++) {
        if (i > 0) await new Promise(resolve => setTimeout(resolve, 6000));
        const range = ranges[i];
        turnover[range.key] = await fetchTurnover(range);
      }
      turnover.error = "";
    } catch (error) {
      turnover.error = String(error && error.message || error);
    } finally {
      turnoverBusy = false;
    }
  }

  const bridge = {
    info() {
      return { ok: true, bridgeVersion: 3, sessionId, bundle: bundle.split("/").pop().split("?")[0] };
    },
    poll() {
      const controllers = allTableControllers();
      const compatibility = inspectCompatibility(controllers);
      const tables = controllers.map(ctrl => { hookTable(ctrl); return tableSnapshot(ctrl); });
      const playerInfo = gameManager.PlayerInfo;
      const hasUserId = playerInfo && playerInfo.userId !== null && playerInfo.userId !== undefined
        && String(playerInfo.userId).length > 0;
      const loggedIn = Boolean(hasUserId || playerInfo && playerInfo.limitkey && tables.length > 0);
      const socketConnected = Boolean(gameManager.IsInSocket);
      return {
        ready: loggedIn && tables.length > 0 && socketConnected,
        loggedIn,
        socketConnected,
        bridgeVersion: compatibility.bridgeVersion,
        observationCompatible: compatibility.observationCompatible,
        bettingCompatible: compatibility.bettingCompatible,
        compatibilityError: compatibility.compatibilityError,
        sessionId,
        bundle: bundle.split("/").pop().split("?")[0],
        balance: finite(playerInfo && playerInfo.sumAmount) || 0,
        tables,
        events: events.splice(0, events.length),
        turnover: { today: turnover.today, week: turnover.week, error: turnover.error }
      };
    },
    // 只触发后台查询并立即返回，结果在后续 poll() 的 turnover 里。
    requestTurnover(ranges) {
      if (turnoverBusy) return { started: false };
      const valid = Array.from(ranges || []).filter(range => range && (range.key === "today" || range.key === "week")
        && /^\d{4}-\d{1,2}-\d{1,2}$/.test(range.start) && /^\d{4}-\d{1,2}-\d{1,2}$/.test(range.end));
      if (valid.length === 0) return { started: false };
      for (const range of valid) {
        const current = turnover[range.key];
        if (current && (current.start !== range.start || current.end !== range.end)) turnover[range.key] = null;
      }
      turnoverBusy = true;
      refreshTurnover(valid);
      return { started: true };
    },
    submitBet(request) {
      if (!request) return { submitted: false, error: "订单参数为空。" };
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
      // The site only acks inside the betting window, so a pending order from an earlier round or shoe
      // will never be acked. The host has already timed it out; drop it so the table can bet again.
      const round = ctrl.tableRoundInfoBean;
      for (let i = pendingOrders.length - 1; i >= 0; i--) {
        const order = pendingOrders[i];
        if (order.tableId === tableId && round
            && (order.shoeSeq !== Number(round.shoeSeq) || order.gameSeq !== Number(round.gameSeq)))
          pendingOrders.splice(i, 1);
      }
      if (pendingOrders.some(order => order.tableId === tableId))
        return { submitted: false, error: "该桌台已有订单等待网站确认。" };
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
      const pending = { orderKey, tableId, shoeSeq, gameSeq };
      pendingOrders.push(pending);
      try {
        ctrl.reqBetMessage(1);
        return { submitted: true, error: "" };
      } catch (error) {
        const index = pendingOrders.indexOf(pending);
        if (index >= 0) pendingOrders.splice(index, 1);
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
