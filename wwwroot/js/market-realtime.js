/**
 * Real-Time NSE-BSE Live Price Streaming, Investor Broadcasting & Buy-Sell Simulation Engine
 * IPO Investment Management System
 */

(function () {
    "use strict";

    const state = {
        signalrConnection: null,
        isPolling: false,
        pollIntervalMs: 3000,
        lastPrices: {}, // ipoId -> { nse: number, bse: number }
        stocks: [],
        serverTime: "",
        isMarketOpen: true,
        userRole: "Investor",
        cashBalance: 100000.00
    };

    // Format currency in Indian Rupees
    function formatINR(val, decimals = 2) {
        if (val === null || val === undefined || isNaN(val)) return "₹0.00";
        return "₹" + Number(val).toLocaleString("en-IN", {
            minimumFractionDigits: decimals,
            maximumFractionDigits: decimals
        });
    }

    function formatNumber(val) {
        if (val === null || val === undefined || isNaN(val)) return "0";
        return Number(val).toLocaleString("en-IN");
    }

    // Initialize Real-time services on DOM Ready
    document.addEventListener("DOMContentLoaded", function () {
        // initTickerBar(); // Disabled: NSE/BSE live headlines ticker bar removed from all dashboards
        initBroadcastContainer();
        initTradeModal();
        startRealtimeConnection();
    });

    // =========================================================================
    // 1. SIGNALR & POLLING CONNECTION
    // =========================================================================

    function startRealtimeConnection() {
        if (window.signalR) {
            connectSignalR();
        } else {
            // Dynamically load SignalR client if not already loaded
            const script = document.createElement("script");
            script.src = "https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.7/signalr.min.js";
            script.onload = function () {
                connectSignalR();
            };
            script.onerror = function () {
                console.warn("[Realtime] CDN SignalR unavailable; falling back to automatic live polling.");
                startPollingFallback();
            };
            document.head.appendChild(script);
        }
    }

    function connectSignalR() {
        try {
            state.signalrConnection = new signalR.HubConnectionBuilder()
                .withUrl("/hubs/market")
                .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
                .configureLogging(signalR.LogLevel.Warning)
                .build();

            state.signalrConnection.on("ReceiveMarketUpdate", function (data) {
                handleMarketUpdate(data);
            });

            state.signalrConnection.on("ReceiveTradeNotification", function (data) {
                handleTradeNotification(data);
            });

            state.signalrConnection.on("ReceiveBroadcastMessage", function (data) {
                handleBroadcastMessage(data);
            });

            state.signalrConnection.start()
                .then(function () {
                    console.log("[Realtime] Connected to SignalR MarketHub.");
                    updateConnectionStatus(true);
                    state.signalrConnection.invoke("JoinMarketFeed").catch(function (e) {
                        console.warn("JoinMarketFeed warning", e);
                    });
                })
                .catch(function (err) {
                    console.warn("[Realtime] SignalR start error, starting polling fallback.", err);
                    updateConnectionStatus(false);
                    startPollingFallback();
                });

            state.signalrConnection.onreconnecting(function () {
                updateConnectionStatus(false, "Reconnecting...");
                if (!state.isPolling) startPollingFallback();
            });

            state.signalrConnection.onreconnected(function () {
                updateConnectionStatus(true);
                stopPollingFallback();
            });

            state.signalrConnection.onclose(function () {
                updateConnectionStatus(false, "Disconnected");
                startPollingFallback();
            });
        } catch (e) {
            console.error("[Realtime] SignalR initialization exception", e);
            startPollingFallback();
        }
    }

    function startPollingFallback() {
        if (state.isPolling) return;
        state.isPolling = true;
        console.log("[Realtime] Live Polling active (fallback mode).");
        pollMarketData();
    }

    function stopPollingFallback() {
        state.isPolling = false;
    }

    function pollMarketData() {
        if (!state.isPolling) return;
        fetch("/api/market/live")
            .then(res => res.json())
            .then(data => {
                handleMarketUpdate(data);
                updateConnectionStatus(true, "Live (HTTP Stream)");
            })
            .catch(err => {
                console.warn("[Realtime] Poll error", err);
                updateConnectionStatus(false, "Offline");
            })
            .finally(() => {
                if (state.isPolling) {
                    setTimeout(pollMarketData, state.pollIntervalMs);
                }
            });
    }

    function updateConnectionStatus(online, label) {
        const dot = document.querySelector(".market-stream-indicator");
        if (dot) {
            dot.className = "market-stream-indicator " + (online ? "online" : "offline");
            dot.title = label || (online ? "Live Feed Connected" : "Connecting to market feed...");
        }
        const textElem = document.querySelector(".market-stream-label");
        if (textElem) {
            textElem.textContent = label || (online ? "● LIVE FEED" : "● CONNECTING...");
        }
    }

    // =========================================================================
    // 2. LIVE PRICE UPDATES & ANIMATIONS
    // =========================================================================

    function handleMarketUpdate(data) {
        if (!data) return;

        state.isMarketOpen = data.isMarketOpen !== false;
        state.serverTime = data.serverTime || "";

        // Update card status badges across dashboards
        document.querySelectorAll(".live-market-stat-open b").forEach(b => {
            if (state.isMarketOpen) {
                b.className = "live-market-positive";
                b.innerHTML = "<i></i> Open";
            } else {
                b.className = "live-market-negative";
                b.innerHTML = '<i style="background:#ef4444;"></i> Closed';
            }
        });

        if (!state.isMarketOpen) {
            updateConnectionStatus(false, "○ MARKET CLOSED (09:30 - 15:30 IST)");
            updateTickerTapeClosed();
            return;
        }

        updateConnectionStatus(true, "● LIVE FEED CONNECTED (NSE & BSE)");

        if (!data.stocks || data.stocks.length === 0) return;

        state.stocks = data.stocks;
        updateTickerTape(data.stocks);

        data.stocks.forEach(function (stock) {
            const ipoId = stock.ipoId;
            const prev = state.lastPrices[ipoId] || { nse: stock.nsePrice, bse: stock.bsePrice };

            const nseChanged = prev.nse !== stock.nsePrice;
            const nseIncreased = stock.nsePrice > prev.nse;

            const bseChanged = prev.bse !== stock.bsePrice;
            const bseIncreased = stock.bsePrice > prev.bse;

            // Update all DOM elements registered for this IPO
            updateStockDOMElements(stock, {
                nseChanged,
                nseIncreased,
                bseChanged,
                bseIncreased
            });

            state.lastPrices[ipoId] = { nse: stock.nsePrice, bse: stock.bsePrice };
        });

        // Recalculate portfolio if on Portfolio page
        recalculatePortfolioMetrics();
    }

    function updateStockDOMElements(stock, flags) {
        const ipoId = stock.ipoId;

        // 1. Update NSE Prices
        document.querySelectorAll(`[data-nse-price="${ipoId}"]`).forEach(el => {
            el.textContent = formatINR(stock.nsePrice);
            if (flags.nseChanged) {
                flashElement(el, flags.nseIncreased ? "flash-up" : "flash-down");
            }
        });

        // 2. Update BSE Prices
        document.querySelectorAll(`[data-bse-price="${ipoId}"]`).forEach(el => {
            el.textContent = formatINR(stock.bsePrice);
            if (flags.bseChanged) {
                flashElement(el, flags.bseIncreased ? "flash-up" : "flash-down");
            }
        });

        // 3. Update Generic Price (defaults to NSE)
        document.querySelectorAll(`[data-live-price="${ipoId}"]`).forEach(el => {
            el.textContent = formatINR(stock.nsePrice);
            if (flags.nseChanged) {
                flashElement(el, flags.nseIncreased ? "flash-up" : "flash-down");
            }
        });

        // 4. Update Price Changes & Percentages
        document.querySelectorAll(`[data-nse-change="${ipoId}"]`).forEach(el => {
            const prefix = stock.nseChange >= 0 ? "+" : "";
            el.textContent = `${prefix}${formatINR(stock.nseChange)} (${stock.nseChangePercent >= 0 ? "+" : ""}${stock.nseChangePercent.toFixed(2)}%)`;
            el.className = el.className.replace(/profit|loss|live-market-positive|live-market-negative/g, "").trim() +
                (stock.nseChange >= 0 ? " profit live-market-positive" : " loss live-market-negative");
        });

        document.querySelectorAll(`[data-bse-change="${ipoId}"]`).forEach(el => {
            const prefix = stock.bseChange >= 0 ? "+" : "";
            el.textContent = `${prefix}${formatINR(stock.bseChange)} (${stock.bseChangePercent >= 0 ? "+" : ""}${stock.bseChangePercent.toFixed(2)}%)`;
            el.className = el.className.replace(/profit|loss|live-market-positive|live-market-negative/g, "").trim() +
                (stock.bseChange >= 0 ? " profit live-market-positive" : " loss live-market-negative");
        });

        // 5. Update High / Low & Volume
        document.querySelectorAll(`[data-live-highlow="${ipoId}"]`).forEach(el => {
            el.textContent = `${formatINR(stock.nseHigh)} / ${formatINR(stock.nseLow)}`;
        });

        document.querySelectorAll(`[data-live-volume="${ipoId}"]`).forEach(el => {
            el.textContent = formatNumber(stock.nseVolume + stock.bseVolume);
        });

        // 6. Update Active Fast Trade Modal if open for this IPO
        const activeModalIpo = document.getElementById("fast-trade-ipo-id");
        if (activeModalIpo && Number(activeModalIpo.value) === ipoId) {
            updateFastTradeModalPrices(stock);
        }

        // 7. Update Active Stock Live Chart Modal if open for this IPO
        if (state.currentChartStock && state.currentChartStock.ipoId === ipoId) {
            updateLiveChartFromTick(stock, flags);
        }
    }

    function flashElement(el, className) {
        el.classList.remove("flash-up", "flash-down");
        // Force reflow
        void el.offsetWidth;
        el.classList.add(className);
        setTimeout(() => el.classList.remove(className), 1200);
    }

    // =========================================================================
    // 3. LIVE TICKER TAPE
    // =========================================================================

    function initTickerBar() {
        // Disabled: NSE/BSE live headlines ticker bar removed from all dashboards
        const existing = document.getElementById("market-ticker-bar");
        if (existing) {
            existing.remove();
        }
    }

    function updateTickerTape(stocks) {
        const track = document.getElementById("ticker-track");
        if (!track || !stocks || stocks.length === 0) return;

        let html = "";
        stocks.forEach(stock => {
            const isUp = stock.nseChange >= 0;
            const sign = isUp ? "+" : "";
            const arrow = isUp ? "▲" : "▼";
            const colorClass = isUp ? "ticker-up" : "ticker-down";

            html += `
                <div class="ticker-item" onclick="window.openFastTrade(${stock.ipoId}, '${escapeHtml(stock.ipoName)}', '${escapeHtml(stock.stockSymbol)}', ${stock.nsePrice}, ${stock.bsePrice})">
                    <span class="ticker-sym">${escapeHtml(stock.stockSymbol || stock.ipoName)}</span>
                    <span class="ticker-exchange">NSE</span>
                    <strong class="ticker-price">${formatINR(stock.nsePrice)}</strong>
                    <span class="ticker-exchange">BSE</span>
                    <strong class="ticker-price">${formatINR(stock.bsePrice)}</strong>
                    <span class="ticker-change ${colorClass}">${arrow} ${sign}${stock.nseChangePercent.toFixed(2)}%</span>
                </div>
            `;
        });

        // Duplicate for seamless infinite loop scroll
        track.innerHTML = html + html;
    }

    function updateTickerTapeClosed() {
        const track = document.getElementById("ticker-track");
        if (!track) return;
        track.innerHTML = `
            <div class="ticker-item" style="color:#94a3b8; font-size:0.75rem;">
                <span class="ticker-exchange" style="background:#475569; color:#f8fafc;">MARKET CLOSED</span>
                <span style="color:#cbd5e1; margin-left:8px;">Official Session: Monday &ndash; Friday, 09:30 AM to 03:30 PM IST</span>
                <span class="ticker-exchange" style="background:#0f766e; color:#f8fafc; margin-left:12px;">NEXT LIVE SESSION: 09:30 AM IST</span>
            </div>
        `;
    }

    function escapeHtml(str) {
        if (!str) return "";
        return str.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
    }

    // =========================================================================
    // 4. REAL-TIME INVESTOR BROADCASTING (ADMIN -> INVESTORS)
    // =========================================================================

    function initBroadcastContainer() {
        if (document.getElementById("broadcast-toast-container")) return;

        const container = document.createElement("div");
        container.id = "broadcast-toast-container";
        container.className = "broadcast-toast-container";
        document.body.appendChild(container);
    }

    function handleBroadcastMessage(data) {
        initBroadcastContainer();
        const container = document.getElementById("broadcast-toast-container");
        if (!container) return;

        const toast = document.createElement("div");
        const category = (data.category || "Announcement").toUpperCase();
        const isUrgent = category.includes("ALERT") || category.includes("URGENT");
        toast.className = `broadcast-toast ${isUrgent ? "broadcast-urgent" : "broadcast-normal"}`;

        toast.innerHTML = `
            <div class="broadcast-toast-head">
                <span class="broadcast-badge">${escapeHtml(category)}</span>
                <span class="broadcast-time">${escapeHtml(data.timestamp || "Just now")}</span>
                <button type="button" class="broadcast-close" aria-label="Dismiss">&times;</button>
            </div>
            <h4 class="broadcast-title">${escapeHtml(data.title)}</h4>
            <p class="broadcast-body">${escapeHtml(data.message)}</p>
        `;

        const closeBtn = toast.querySelector(".broadcast-close");
        closeBtn.onclick = function () {
            toast.classList.add("broadcast-fadeout");
            setTimeout(() => toast.remove(), 400);
        };

        container.appendChild(toast);

        // Auto remove after 9 seconds
        setTimeout(() => {
            if (toast.parentElement) {
                toast.classList.add("broadcast-fadeout");
                setTimeout(() => toast.remove(), 400);
            }
        }, 9000);
    }

    function handleTradeNotification(data) {
        initBroadcastContainer();
        const container = document.getElementById("broadcast-toast-container");
        if (!container) return;

        const isBuy = data.tradeType === "BUY";
        const toast = document.createElement("div");
        toast.className = `broadcast-toast broadcast-trade-toast ${isBuy ? "trade-buy" : "trade-sell"}`;

        toast.innerHTML = `
            <div class="broadcast-toast-head">
                <span class="broadcast-badge trade-badge ${isBuy ? "badge-buy" : "badge-sell"}">${isBuy ? "LIVE BUY" : "LIVE SELL"} · ${escapeHtml(data.exchange)}</span>
                <span class="broadcast-time">${escapeHtml(data.timestamp || "Just now")}</span>
                <button type="button" class="broadcast-close" aria-label="Dismiss">&times;</button>
            </div>
            <p class="broadcast-trade-msg">
                <strong>${escapeHtml(data.user)}</strong> executed <b>${formatNumber(data.quantity)} shares</b> of 
                <strong>${escapeHtml(data.stockName)}</strong> at <b>${formatINR(data.price)}</b> (${formatINR(data.totalAmount)})
            </p>
        `;

        toast.querySelector(".broadcast-close").onclick = () => {
            toast.classList.add("broadcast-fadeout");
            setTimeout(() => toast.remove(), 400);
        };

        container.appendChild(toast);
        setTimeout(() => {
            if (toast.parentElement) {
                toast.classList.add("broadcast-fadeout");
                setTimeout(() => toast.remove(), 400);
            }
        }, 6000);
    }

    // =========================================================================
    // 5. DYNAMIC PORTFOLIO RECALCULATION
    // =========================================================================

    function recalculatePortfolioMetrics() {
        const rows = document.querySelectorAll(".portfolio-table-row[data-holding-id]");
        if (rows.length === 0) return;

        let totalCurrentValue = 0;
        let totalInvested = 0;
        let totalUnrealizedPL = 0;

        rows.forEach(row => {
            const ipoId = Number(row.dataset.ipoId);
            const qty = Number(row.dataset.holdingQty || 0);
            const avgBuy = Number(row.dataset.holdingAvgBuy || 0);
            const investedAmount = qty * avgBuy;
            totalInvested += investedAmount;

            const stock = state.stocks.find(s => s.ipoId === ipoId);
            let currentPrice = avgBuy;
            if (stock) {
                // Default to NSE price
                currentPrice = stock.nsePrice;
            }

            const currentValue = qty * currentPrice;
            const unrealizedPL = currentValue - investedAmount;
            totalCurrentValue += currentValue;
            totalUnrealizedPL += unrealizedPL;

            // Update row current price
            const priceEl = row.querySelector(".holding-live-price");
            if (priceEl) priceEl.textContent = formatINR(currentPrice);

            // Update row current value
            const valEl = row.querySelector(".holding-live-value");
            if (valEl) valEl.textContent = formatINR(currentValue);

            // Update row P/L
            const plEl = row.querySelector(".holding-live-pl");
            if (plEl) {
                const sign = unrealizedPL >= 0 ? "+" : "-";
                plEl.textContent = `${sign}${formatINR(Math.Abs ? Math.abs(unrealizedPL) : Math.abs(unrealizedPL))}`;
                plEl.className = "holding-live-pl " + (unrealizedPL >= 0 ? "profit" : "loss");
            }
        });

        // Update Summary Cards
        const totalValueEl = document.querySelector("[data-portfolio-current-value]");
        if (totalValueEl) totalValueEl.textContent = formatINR(totalCurrentValue);

        const totalUnrealizedEl = document.querySelector("[data-portfolio-unrealized-pl]");
        if (totalUnrealizedEl) {
            const sign = totalUnrealizedPL >= 0 ? "+" : "-";
            totalUnrealizedEl.textContent = `${sign}${formatINR(Math.abs(totalUnrealizedPL))}`;
            totalUnrealizedEl.className = totalUnrealizedPL >= 0 ? "profit" : "loss";
        }

        const totalReturnEl = document.querySelector("[data-portfolio-total-return]");
        const realizedPLEl = document.querySelector("[data-portfolio-realized-pl]");
        const realizedPL = realizedPLEl ? (parseFloat(realizedPLEl.dataset.value) || 0) : 0;

        if (totalReturnEl) {
            const totalReturn = realizedPL + totalUnrealizedPL;
            const sign = totalReturn >= 0 ? "+" : "-";
            totalReturnEl.textContent = `${sign}${formatINR(Math.abs(totalReturn))}`;
            totalReturnEl.className = totalReturn >= 0 ? "profit" : "loss";
        }
    }

    // =========================================================================
    // 6. FAST TRADE MODAL (BUY / SELL SIMULATION)
    // =========================================================================

    function initTradeModal() {
        if (document.getElementById("fast-trade-modal")) return;

        const modalDiv = document.createElement("div");
        modalDiv.id = "fast-trade-modal";
        modalDiv.className = "fast-trade-modal-overlay hidden";
        modalDiv.innerHTML = `
            <div class="fast-trade-dialog">
                <div class="fast-trade-header">
                    <div>
                        <span class="fast-trade-badge">SIMULATED TRADING</span>
                        <h3 id="fast-trade-title">Buy / Sell Shares</h3>
                        <small id="fast-trade-subtitle">Verified live exchange pricing</small>
                    </div>
                    <button type="button" class="fast-trade-close-btn" onclick="window.closeFastTrade()">&times;</button>
                </div>

                <div class="fast-trade-body">
                    <!-- Exchange Switcher -->
                    <div class="exchange-selector">
                        <button type="button" class="exchange-tab active" id="tab-nse" onclick="window.selectTradeExchange('NSE')">
                            <strong>NSE</strong>
                            <span id="fast-trade-nse-price">₹0.00</span>
                        </button>
                        <button type="button" class="exchange-tab" id="tab-bse" onclick="window.selectTradeExchange('BSE')">
                            <strong>BSE</strong>
                            <span id="fast-trade-bse-price">₹0.00</span>
                        </button>
                    </div>

                    <!-- Trade Type Switcher -->
                    <div class="trade-type-selector">
                        <button type="button" class="trade-type-btn btn-buy active" id="btn-trade-buy" onclick="window.setTradeAction('BUY')">BUY</button>
                        <button type="button" class="trade-type-btn btn-sell" id="btn-trade-sell" onclick="window.setTradeAction('SELL')">SELL</button>
                    </div>

                    <input type="hidden" id="fast-trade-ipo-id" value="" />
                    <input type="hidden" id="fast-trade-investment-id" value="" />
                    <input type="hidden" id="fast-trade-selected-exchange" value="NSE" />
                    <input type="hidden" id="fast-trade-selected-action" value="BUY" />
                    <input type="hidden" id="fast-trade-available-shares" value="0" />

                    <!-- Quantity Section -->
                    <div class="trade-field-group">
                        <label for="fast-trade-quantity">Quantity (Shares)</label>
                        <div class="quantity-input-wrap">
                            <button type="button" class="qty-btn" onclick="window.adjustTradeQty(-10)">-10</button>
                            <button type="button" class="qty-btn" onclick="window.adjustTradeQty(-1)">-1</button>
                            <input type="number" id="fast-trade-quantity" value="10" min="1" oninput="window.calcTradeTotal()" />
                            <button type="button" class="qty-btn" onclick="window.adjustTradeQty(1)">+1</button>
                            <button type="button" class="qty-btn" onclick="window.adjustTradeQty(10)">+10</button>
                            <button type="button" class="qty-btn" onclick="window.adjustTradeQty(50)">+50</button>
                        </div>
                        <small id="fast-trade-avail-note" class="trade-field-hint">Available cash balance: loading...</small>
                    </div>

                    <!-- Summary Card -->
                    <div class="trade-summary-card">
                        <div class="trade-summary-row">
                            <span>Selected Exchange</span>
                            <strong id="trade-summary-exchange">NSE</strong>
                        </div>
                        <div class="trade-summary-row">
                            <span>Execution Price</span>
                            <strong id="trade-summary-price">₹0.00</strong>
                        </div>
                        <div class="trade-summary-row">
                            <span>Estimated Total</span>
                            <strong id="trade-summary-total" class="trade-total-highlight">₹0.00</strong>
                        </div>
                    </div>

                    <div id="fast-trade-alert" class="fast-trade-msg hidden"></div>

                    <!-- Action Buttons -->
                    <div class="fast-trade-actions">
                        <button type="button" class="btn btn-trade-submit" id="btn-trade-execute" onclick="window.submitFastTrade()">Confirm & Execute Trade</button>
                        <button type="button" class="btn btn-secondary" onclick="window.closeFastTrade()">Cancel</button>
                    </div>
                </div>
            </div>
        `;

        document.body.appendChild(modalDiv);
    }

    window.openFastTrade = function (ipoId, ipoName, symbol, nsePrice, bsePrice, availableShares, investmentId, defaultAction) {
        if (!state.isMarketOpen) {
            alert("Market is currently Closed.\n\nLive trading and share price execution are available strictly during official trading hours: Monday – Friday, 09:30 AM to 03:30 PM IST.");
            return;
        }
        initTradeModal();
        const modal = document.getElementById("fast-trade-modal");
        if (!modal) return;

        document.getElementById("fast-trade-ipo-id").value = ipoId;
        document.getElementById("fast-trade-investment-id").value = investmentId || "";
        document.getElementById("fast-trade-available-shares").value = availableShares || 0;

        document.getElementById("fast-trade-title").textContent = `${ipoName} (${symbol})`;
        document.getElementById("fast-trade-subtitle").textContent = "Simulated Exchange Execution";

        document.getElementById("fast-trade-nse-price").textContent = formatINR(nsePrice);
        document.getElementById("fast-trade-bse-price").textContent = formatINR(bsePrice);

        window.selectTradeExchange("NSE");
        window.setTradeAction(defaultAction || "BUY");

        const qtyInput = document.getElementById("fast-trade-quantity");
        if (defaultAction === "SELL" && availableShares > 0) {
            qtyInput.value = Math.min(10, availableShares);
        } else {
            qtyInput.value = 10;
        }

        window.calcTradeTotal();

        modal.classList.remove("hidden");
    };

    window.closeFastTrade = function () {
        const modal = document.getElementById("fast-trade-modal");
        if (modal) modal.classList.add("hidden");
        const alertBox = document.getElementById("fast-trade-alert");
        if (alertBox) alertBox.className = "fast-trade-msg hidden";
    };

    window.selectTradeExchange = function (exchange) {
        document.getElementById("fast-trade-selected-exchange").value = exchange;
        document.getElementById("tab-nse").classList.toggle("active", exchange === "NSE");
        document.getElementById("tab-bse").classList.toggle("active", exchange === "BSE");
        document.getElementById("trade-summary-exchange").textContent = exchange;
        window.calcTradeTotal();
    };

    window.setTradeAction = function (action) {
        document.getElementById("fast-trade-selected-action").value = action;
        const isBuy = action === "BUY";
        document.getElementById("btn-trade-buy").classList.toggle("active", isBuy);
        document.getElementById("btn-trade-sell").classList.toggle("active", !isBuy);

        const execBtn = document.getElementById("btn-trade-execute");
        execBtn.className = "btn btn-trade-submit " + (isBuy ? "btn-execute-buy" : "btn-execute-sell");
        execBtn.textContent = isBuy ? "Execute Simulated Buy" : "Execute Simulated Sell";

        const availNote = document.getElementById("fast-trade-avail-note");
        const availShares = Number(document.getElementById("fast-trade-available-shares").value || 0);

        if (isBuy) {
            availNote.textContent = "Orders execute at verified real-time tick price.";
        } else {
            availNote.textContent = `Available holding to sell: ${formatNumber(availShares)} shares.`;
        }

        window.calcTradeTotal();
    };

    window.adjustTradeQty = function (delta) {
        const input = document.getElementById("fast-trade-quantity");
        const current = Number(input.value) || 0;
        const action = document.getElementById("fast-trade-selected-action").value;
        const maxAvail = Number(document.getElementById("fast-trade-available-shares").value || 0);

        let next = Math.max(1, current + delta);
        if (action === "SELL" && maxAvail > 0) {
            next = Math.min(next, maxAvail);
        }
        input.value = next;
        window.calcTradeTotal();
    };

    window.calcTradeTotal = function () {
        const exchange = document.getElementById("fast-trade-selected-exchange")?.value || "NSE";
        const priceEl = document.getElementById(exchange === "BSE" ? "fast-trade-bse-price" : "fast-trade-nse-price");
        const rawPriceText = priceEl?.textContent.replace(/[^0-9.]/g, "") || "0";
        const price = parseFloat(rawPriceText) || 0;

        const qtyInput = document.getElementById("fast-trade-quantity");
        const qty = Math.max(1, parseInt(qtyInput?.value || "1", 10));

        const total = qty * price;
        const priceSum = document.getElementById("trade-summary-price");
        const totalSum = document.getElementById("trade-summary-total");

        if (priceSum) priceSum.textContent = formatINR(price);
        if (totalSum) totalSum.textContent = formatINR(total);
    };

    function updateFastTradeModalPrices(stock) {
        const nseEl = document.getElementById("fast-trade-nse-price");
        const bseEl = document.getElementById("fast-trade-bse-price");
        if (nseEl) nseEl.textContent = formatINR(stock.nsePrice);
        if (bseEl) bseEl.textContent = formatINR(stock.bsePrice);
        window.calcTradeTotal();
    }

    window.submitFastTrade = function () {
        const ipoId = parseInt(document.getElementById("fast-trade-ipo-id").value, 10);
        const investmentId = parseInt(document.getElementById("fast-trade-investment-id").value, 10) || null;
        const exchange = document.getElementById("fast-trade-selected-exchange").value;
        const tradeType = document.getElementById("fast-trade-selected-action").value;
        const quantity = parseInt(document.getElementById("fast-trade-quantity").value, 10);

        const alertBox = document.getElementById("fast-trade-alert");
        const execBtn = document.getElementById("btn-trade-execute");

        if (!ipoId || quantity <= 0) {
            alertBox.className = "fast-trade-msg error";
            alertBox.textContent = "Please enter a valid quantity.";
            return;
        }

        execBtn.disabled = true;
        execBtn.textContent = "Executing...";

        fetch("/api/market/trade", {
            method: "POST",
            headers: {
                "Content-Type": "application/json"
            },
            body: JSON.stringify({
                ipoId: ipoId,
                exchange: exchange,
                tradeType: tradeType,
                quantity: quantity,
                investmentId: investmentId
            })
        })
            .then(res => res.json())
            .then(data => {
                if (data.success) {
                    alertBox.className = "fast-trade-msg success";
                    alertBox.textContent = data.message;

                    // Update local balance display if on page
                    document.querySelectorAll("[data-cash-balance]").forEach(el => {
                        el.textContent = formatINR(data.newCashBalance);
                    });

                    setTimeout(() => {
                        window.closeFastTrade();
                        // Refresh page or view
                        if (window.location.pathname.toLowerCase().includes("portfolio")) {
                            window.location.reload();
                        }
                    }, 1400);
                } else {
                    alertBox.className = "fast-trade-msg error";
                    alertBox.textContent = data.message || "Trade could not be completed.";
                    execBtn.disabled = false;
                    execBtn.textContent = "Retry Trade";
                }
            })
            .catch(err => {
                alertBox.className = "fast-trade-msg error";
                alertBox.textContent = "Network error communicating with the trading engine.";
                execBtn.disabled = false;
                execBtn.textContent = "Retry Trade";
            });
    };

    // =========================================================================
    // 7. ADMIN REAL-TIME BROADCAST TRIGGER
    // =========================================================================

    window.sendAdminRealtimeBroadcast = function (form) {
        if (!form) return false;
        const titleInput = form.querySelector("[name='Title'], [name='title']");
        const msgInput = form.querySelector("[name='Message'], [name='message']");
        const catSelect = form.querySelector("[name='Category'], [name='category']");
        const statusEl = form.querySelector(".broadcast-send-status");

        if (!titleInput?.value || !msgInput?.value) {
            alert("Please fill in both the title and announcement message.");
            return false;
        }

        const title = titleInput.value.trim();
        const message = msgInput.value.trim();
        const category = catSelect?.value || "Announcement";

        if (statusEl) {
            statusEl.textContent = "Broadcasting to active investors in real-time...";
            statusEl.className = "broadcast-send-status text-info";
        }

        fetch("/api/market/broadcast", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ title, message, category })
        })
            .then(res => res.json())
            .then(data => {
                if (data.success) {
                    if (statusEl) {
                        statusEl.textContent = `✓ Broadcast delivered in real time to ${data.recipientCount} investors!`;
                        statusEl.className = "broadcast-send-status text-success";
                    }
                    titleInput.value = "";
                    msgInput.value = "";
                } else {
                    if (statusEl) {
                        statusEl.textContent = "✗ Error: " + (data.message || "Failed to broadcast");
                        statusEl.className = "broadcast-send-status text-danger";
                    }
                }
            })
            .catch(err => {
                if (statusEl) {
                    statusEl.textContent = "✗ Network error executing broadcast.";
                    statusEl.className = "broadcast-send-status text-danger";
                }
            });

        return false;
    };

    window.resetSimulatedCashBalance = function () {
        alert("Unrestricted investor wallet resets are disabled. Virtual balances can only be adjusted by a platform Administrator with an auditable reason.");
    };

    // =========================================================================
    // 8. GROWW-STYLE REAL-TIME STOCK LIVE CHART ENGINE
    // =========================================================================
    state.currentChartStock = null;
    let chartCachedData = null;
    let canvasEventsAttached = false;

    window.openStockLiveChart = function (ipoId, symbol, companyName, ipoName) {
        state.currentChartStock = {
            ipoId: Number(ipoId),
            symbol: symbol || "STOCK",
            companyName: companyName || "Company",
            ipoName: ipoName || symbol || "IPO",
            exchange: "NSE",
            timeframe: "1D",
            isCandle: false
        };

        const initials = (symbol || "ST").slice(0, 2).toUpperCase();
        const initialsEl = document.getElementById("chart-logo-initials");
        if (initialsEl) initialsEl.textContent = initials;

        const symEl = document.getElementById("chart-symbol-text");
        if (symEl) symEl.textContent = symbol || "STOCK";

        const compEl = document.getElementById("chart-company-text");
        if (compEl) compEl.textContent = companyName || ipoName || "Company";

        const exchLabel = document.getElementById("chart-exchange-label");
        if (exchLabel) exchLabel.textContent = "NSE";

        // Reset timeframe pills to 1D
        document.querySelectorAll(".tf-pill").forEach(p => {
            p.classList.toggle("active", p.dataset.tf === "1D");
        });

        // Reset tab view to Overview
        window.switchStockChartTab("overview");

        // Hide live market modal if open to prevent stacked modal conflicts
        const liveMarketModalEl = document.getElementById("live-market-modal");
        if (liveMarketModalEl && window.bootstrap) {
            const instance = bootstrap.Modal.getInstance(liveMarketModalEl);
            if (instance) instance.hide();
        }

        const chartModalEl = document.getElementById("stock-live-chart-modal");
        if (chartModalEl) {
            if (window.bootstrap) {
                const modal = bootstrap.Modal.getOrCreateInstance(chartModalEl);
                modal.show();
            } else {
                chartModalEl.classList.add("show");
                chartModalEl.style.display = "block";
            }
        }

        initCanvasEvents();
        window.loadStockChartData(ipoId, "1D", "NSE");
    };

    window.closeStockLiveChart = function () {
        const chartModalEl = document.getElementById("stock-live-chart-modal");
        if (chartModalEl) {
            if (window.bootstrap) {
                const modal = bootstrap.Modal.getInstance(chartModalEl);
                if (modal) modal.hide();
            } else {
                chartModalEl.classList.remove("show");
                chartModalEl.style.display = "none";
            }
        }

        state.currentChartStock = null;
        chartCachedData = null;

        // Re-open live market modal
        const liveMarketModalEl = document.getElementById("live-market-modal");
        if (liveMarketModalEl && window.bootstrap) {
            const instance = bootstrap.Modal.getOrCreateInstance(liveMarketModalEl);
            instance.show();
        }
    };

    window.selectChartTf = function (timeframe) {
        if (!state.currentChartStock) return;
        state.currentChartStock.timeframe = timeframe;

        document.querySelectorAll(".tf-pill").forEach(p => {
            p.classList.toggle("active", p.dataset.tf === timeframe);
        });

        const tfLabel = document.getElementById("chart-delta-tf");
        if (tfLabel) tfLabel.textContent = timeframe;

        window.loadStockChartData(state.currentChartStock.ipoId, timeframe, state.currentChartStock.exchange);
    };

    window.toggleChartExchange = function () {
        if (!state.currentChartStock) return;
        const newExch = state.currentChartStock.exchange === "NSE" ? "BSE" : "NSE";
        state.currentChartStock.exchange = newExch;

        const exchLabel = document.getElementById("chart-exchange-label");
        if (exchLabel) exchLabel.textContent = newExch;

        window.loadStockChartData(state.currentChartStock.ipoId, state.currentChartStock.timeframe, newExch);
    };

    window.toggleCandleView = function () {
        if (!state.currentChartStock) return;
        state.currentChartStock.isCandle = !state.currentChartStock.isCandle;

        const iconEl = document.getElementById("chart-view-toggle-icon");
        if (iconEl) {
            iconEl.innerHTML = state.currentChartStock.isCandle ? "&#128200;" : "&#128334;";
        }

        if (chartCachedData) {
            renderStockCanvas(chartCachedData);
        }
    };

    window.switchStockChartTab = function (tabName) {
        document.querySelectorAll(".stock-tab-btn").forEach(btn => {
            btn.classList.toggle("active", btn.dataset.tab === tabName);
        });

        document.querySelectorAll(".stock-tab-content").forEach(content => {
            content.classList.toggle("hidden", content.id !== `tab-content-${tabName}`);
        });

        if (tabName === "news") populateNewsTab();
        if (tabName === "events") populateEventsTab();
    };

    window.loadStockChartData = function (ipoId, timeframe, exchange) {
        fetch(`/api/market/chart/${ipoId}?timeframe=${timeframe}&exchange=${exchange}`)
            .then(res => res.json())
            .then(data => {
                chartCachedData = data;
                updateChartDomElements(data);
                renderStockCanvas(data);
            })
            .catch(err => {
                console.error("[StockChart] Error fetching chart data", err);
            });
    };

    function updateChartDomElements(data) {
        if (!data) return;

        // Big Main Price
        const priceEl = document.getElementById("chart-main-price");
        if (priceEl) priceEl.textContent = formatINR(data.currentPrice);

        // Price Change & Percentage
        const deltaValEl = document.getElementById("chart-delta-val");
        const deltaPctEl = document.getElementById("chart-delta-pct");
        const deltaContainer = document.getElementById("chart-price-delta");
        const isUp = data.priceChange >= 0;
        const sign = isUp ? "+" : "";

        if (deltaValEl) deltaValEl.textContent = `${sign}${formatINR(data.priceChange)}`;
        if (deltaPctEl) deltaPctEl.textContent = `(${sign}${data.priceChangePercentage.toFixed(2)}%)`;
        if (deltaContainer) {
            deltaContainer.className = "stock-price-change " + (isUp ? "profit" : "loss");
        }

        // Previous close pill
        const prevPill = document.getElementById("chart-prev-close-val");
        if (prevPill) prevPill.textContent = formatINR(data.previousClose);

        // Today's Low / High Range
        const todayLowEl = document.getElementById("perf-today-low");
        const todayHighEl = document.getElementById("perf-today-high");
        if (todayLowEl) todayLowEl.textContent = formatINR(data.dayLow);
        if (todayHighEl) todayHighEl.textContent = formatINR(data.dayHigh);

        const todaySpan = Math.max(0.1, data.dayHigh - data.dayLow);
        const todayPct = Math.min(100, Math.max(0, (data.currentPrice - data.dayLow) / todaySpan * 100));
        const todayPointer = document.getElementById("perf-today-pointer");
        const todayBar = document.getElementById("perf-today-bar");
        if (todayPointer) todayPointer.style.left = `${todayPct}%`;
        if (todayBar) todayBar.style.width = `${todayPct}%`;

        // 52-Week Low / High Range
        const w52LowEl = document.getElementById("perf-52w-low");
        const w52HighEl = document.getElementById("perf-52w-high");
        if (w52LowEl) w52LowEl.textContent = formatINR(data.low52w);
        if (w52HighEl) w52HighEl.textContent = formatINR(data.high52w);

        const w52Span = Math.max(0.1, data.high52w - data.low52w);
        const w52Pct = Math.min(100, Math.max(0, (data.currentPrice - data.low52w) / w52Span * 100));
        const w52Pointer = document.getElementById("perf-52w-pointer");
        const w52Bar = document.getElementById("perf-52w-bar");
        if (w52Pointer) w52Pointer.style.left = `${w52Pct}%`;
        if (w52Bar) w52Bar.style.width = `${w52Pct}%`;

        // Fundamentals Grid
        const openEl = document.getElementById("perf-open");
        const prevEl = document.getElementById("perf-prev-close");
        const volEl = document.getElementById("perf-volume");
        const lowCircEl = document.getElementById("perf-lower-circuit");
        const upCircEl = document.getElementById("perf-upper-circuit");
        const bidAskEl = document.getElementById("perf-bid-ask");

        if (openEl) openEl.textContent = formatINR(data.openPrice);
        if (prevEl) prevEl.textContent = formatINR(data.previousClose);
        if (volEl) volEl.textContent = formatNumber(data.volume);
        if (lowCircEl) lowCircEl.textContent = formatINR(data.lowerCircuit);
        if (upCircEl) upCircEl.textContent = formatINR(data.upperCircuit);
        if (bidAskEl) bidAskEl.textContent = `${formatINR(data.bestBid)} / ${formatINR(data.bestAsk)}`;

        // Technicals
        const sma20El = document.getElementById("tech-sma20");
        const sma50El = document.getElementById("tech-sma50");
        if (sma20El) sma20El.textContent = formatINR(data.currentPrice * 0.985);
        if (sma50El) sma50El.textContent = formatINR(data.currentPrice * 0.962);
    }

    function renderStockCanvas(data) {
        const canvas = document.getElementById("stock-live-canvas");
        if (!canvas || !data) return;

        const ctx = canvas.getContext("2d");
        const dpr = window.devicePixelRatio || 1;
        const rect = canvas.getBoundingClientRect();

        // Responsive DPI scale
        canvas.width = rect.width * dpr;
        canvas.height = rect.height * dpr;
        ctx.resetTransform();
        ctx.scale(dpr, dpr);

        const w = rect.width;
        const h = rect.height;

        ctx.clearRect(0, 0, w, h);

        const isCandle = state.currentChartStock && state.currentChartStock.isCandle;
        const isUp = (data.currentPrice >= data.previousClose);
        const strokeColor = isUp ? "#00c076" : "#eb5b3c";

        if (isCandle && data.candles && data.candles.length > 0) {
            renderCandlesticks(ctx, data.candles, data.previousClose, w, h);
        } else if (data.points && data.points.length > 0) {
            renderLineAreaChart(ctx, data.points, data.previousClose, data.currentPrice, strokeColor, w, h);
        }
    }

    function renderLineAreaChart(ctx, points, prevClose, currentPrice, strokeColor, w, h) {
        const prices = points.map(p => Number(p.price));
        prices.push(Number(prevClose));

        const minPrice = Math.min(...prices);
        const maxPrice = Math.max(...prices);
        const pricePadding = Math.max(0.2, (maxPrice - minPrice) * 0.12);
        const effectiveMin = minPrice - pricePadding;
        const effectiveMax = maxPrice + pricePadding;
        const priceRange = effectiveMax - effectiveMin || 1;

        const paddingLeft = 14;
        const paddingRight = 14;
        const paddingTop = 20;
        const paddingBottom = 24;
        const plotWidth = w - paddingLeft - paddingRight;
        const plotHeight = h - paddingTop - paddingBottom;

        function getX(index) {
            return paddingLeft + (index / (points.length - 1)) * plotWidth;
        }

        function getY(val) {
            return paddingTop + plotHeight - ((val - effectiveMin) / priceRange) * plotHeight;
        }

        // 1. Draw Previous Close Horizontal Dashed Line
        const prevCloseY = getY(prevClose);
        ctx.save();
        ctx.strokeStyle = "rgba(148, 163, 184, 0.35)";
        ctx.setLineDash([4, 4]);
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(paddingLeft, prevCloseY);
        ctx.lineTo(w - paddingRight, prevCloseY);
        ctx.stroke();
        ctx.restore();

        // 2. Build Smooth Line Path
        const coords = points.map((p, i) => ({ x: getX(i), y: getY(p.price) }));

        // Gradient Fill Under Curve
        ctx.save();
        const grad = ctx.createLinearGradient(0, paddingTop, 0, h - paddingBottom);
        const isUp = strokeColor === "#00c076";
        grad.addColorStop(0, isUp ? "rgba(0, 192, 118, 0.28)" : "rgba(235, 91, 60, 0.28)");
        grad.addColorStop(0.65, isUp ? "rgba(0, 192, 118, 0.08)" : "rgba(235, 91, 60, 0.08)");
        grad.addColorStop(1, "rgba(0, 0, 0, 0.0)");

        ctx.fillStyle = grad;
        ctx.beginPath();
        ctx.moveTo(coords[0].x, h - paddingBottom);
        ctx.lineTo(coords[0].x, coords[0].y);

        for (let i = 0; i < coords.length - 1; i++) {
            const xc = (coords[i].x + coords[i + 1].x) / 2;
            const yc = (coords[i].y + coords[i + 1].y) / 2;
            ctx.quadraticCurveTo(coords[i].x, coords[i].y, xc, yc);
        }
        const last = coords[coords.length - 1];
        ctx.lineTo(last.x, last.y);
        ctx.lineTo(last.x, h - paddingBottom);
        ctx.closePath();
        ctx.fill();
        ctx.restore();

        // 3. Draw Main Line Stroke
        ctx.save();
        ctx.strokeStyle = strokeColor;
        ctx.lineWidth = 2.2;
        ctx.lineJoin = "round";
        ctx.lineCap = "round";
        ctx.shadowColor = strokeColor;
        ctx.shadowBlur = 6;

        ctx.beginPath();
        ctx.moveTo(coords[0].x, coords[0].y);
        for (let i = 0; i < coords.length - 1; i++) {
            const xc = (coords[i].x + coords[i + 1].x) / 2;
            const yc = (coords[i].y + coords[i + 1].y) / 2;
            ctx.quadraticCurveTo(coords[i].x, coords[i].y, xc, yc);
        }
        ctx.lineTo(last.x, last.y);
        ctx.stroke();
        ctx.restore();

        // 4. Glowing Head Dot at Latest Point
        ctx.save();
        ctx.fillStyle = strokeColor;
        ctx.beginPath();
        ctx.arc(last.x, last.y, 4.5, 0, Math.PI * 2);
        ctx.fill();

        ctx.strokeStyle = "#ffffff";
        ctx.lineWidth = 1.8;
        ctx.stroke();
        ctx.restore();
    }

    function renderCandlesticks(ctx, candles, prevClose, w, h) {
        const highs = candles.map(c => c.high);
        const lows = candles.map(c => c.low);
        highs.push(Number(prevClose));
        lows.push(Number(prevClose));

        const minPrice = Math.min(...lows);
        const maxPrice = Math.max(...highs);
        const pricePadding = Math.max(0.2, (maxPrice - minPrice) * 0.12);
        const effectiveMin = minPrice - pricePadding;
        const effectiveMax = maxPrice + pricePadding;
        const priceRange = effectiveMax - effectiveMin || 1;

        const paddingLeft = 16;
        const paddingRight = 16;
        const paddingTop = 20;
        const paddingBottom = 24;
        const plotWidth = w - paddingLeft - paddingRight;
        const plotHeight = h - paddingTop - paddingBottom;

        function getY(val) {
            return paddingTop + plotHeight - ((val - effectiveMin) / priceRange) * plotHeight;
        }

        // Draw Prev Close Dashed Line
        const prevCloseY = getY(prevClose);
        ctx.save();
        ctx.strokeStyle = "rgba(148, 163, 184, 0.35)";
        ctx.setLineDash([4, 4]);
        ctx.beginPath();
        ctx.moveTo(paddingLeft, prevCloseY);
        ctx.lineTo(w - paddingRight, prevCloseY);
        ctx.stroke();
        ctx.restore();

        const candleCount = candles.length;
        const candleWidth = Math.max(2, (plotWidth / candleCount) * 0.65);

        candles.forEach((c, i) => {
            const x = paddingLeft + (i / (candleCount - 1 || 1)) * plotWidth;
            const yOpen = getY(c.open);
            const yClose = getY(c.close);
            const yHigh = getY(c.high);
            const yLow = getY(c.low);
            const isGreen = c.close >= c.open;
            const color = isGreen ? "#00c076" : "#eb5b3c";

            ctx.save();
            ctx.strokeStyle = color;
            ctx.fillStyle = color;
            ctx.lineWidth = 1.2;

            // Wick
            ctx.beginPath();
            ctx.moveTo(x, yHigh);
            ctx.lineTo(x, yLow);
            ctx.stroke();

            // Body
            const top = Math.min(yOpen, yClose);
            const bodyHeight = Math.max(1.5, Math.abs(yClose - yOpen));
            ctx.fillRect(x - candleWidth / 2, top, candleWidth, bodyHeight);
            ctx.restore();
        });
    }

    function initCanvasEvents() {
        if (canvasEventsAttached) return;
        const viewport = document.getElementById("chart-viewport");
        if (!viewport) return;

        const crosshair = document.getElementById("stock-chart-crosshair");
        const tooltip = document.getElementById("stock-chart-tooltip");
        const ttPrice = document.getElementById("tt-price");
        const ttTime = document.getElementById("tt-time");

        function handlePointerMove(e) {
            if (!chartCachedData || !chartCachedData.points || chartCachedData.points.length === 0) return;

            const rect = viewport.getBoundingClientRect();
            const clientX = e.touches ? e.touches[0].clientX : e.clientX;
            const offsetX = clientX - rect.left;

            if (offsetX < 0 || offsetX > rect.width) {
                handlePointerLeave();
                return;
            }

            const points = chartCachedData.points;
            const frac = Math.min(1, Math.max(0, (offsetX - 14) / (rect.width - 28)));
            const index = Math.min(points.length - 1, Math.max(0, Math.round(frac * (points.length - 1))));
            const point = points[index];

            if (crosshair) {
                crosshair.classList.remove("hidden");
                crosshair.style.left = `${offsetX}px`;
            }

            if (tooltip) {
                tooltip.classList.remove("hidden");
                const tooltipX = Math.min(rect.width - 90, Math.max(10, offsetX - 40));
                tooltip.style.left = `${tooltipX}px`;
                if (ttPrice) ttPrice.textContent = formatINR(point.price);
                if (ttTime) ttTime.textContent = point.label;
            }

            // Temporarily update big price display on hover (Groww style)
            const mainPriceEl = document.getElementById("chart-main-price");
            if (mainPriceEl) mainPriceEl.textContent = formatINR(point.price);
        }

        function handlePointerLeave() {
            if (crosshair) crosshair.classList.add("hidden");
            if (tooltip) tooltip.classList.add("hidden");

            // Restore current live price
            if (chartCachedData) {
                const mainPriceEl = document.getElementById("chart-main-price");
                if (mainPriceEl) mainPriceEl.textContent = formatINR(chartCachedData.currentPrice);
            }
        }

        viewport.addEventListener("mousemove", handlePointerMove);
        viewport.addEventListener("mouseleave", handlePointerLeave);
        viewport.addEventListener("touchmove", handlePointerMove, { passive: true });
        viewport.addEventListener("touchend", handlePointerLeave);

        canvasEventsAttached = true;
    }

    function updateLiveChartFromTick(stock, flags) {
        if (!state.currentChartStock || state.currentChartStock.ipoId !== stock.ipoId) return;

        const isBse = state.currentChartStock.exchange === "BSE";
        const newPrice = isBse ? stock.bsePrice : stock.nsePrice;
        const newChange = isBse ? stock.bseChange : stock.nseChange;
        const newPct = isBse ? stock.bseChangePercent : stock.nseChangePercent;

        // Animate big price
        const priceEl = document.getElementById("chart-main-price");
        if (priceEl) {
            priceEl.textContent = formatINR(newPrice);
            const changed = isBse ? flags.bseChanged : flags.nseChanged;
            const increased = isBse ? flags.bseIncreased : flags.nseIncreased;
            if (changed) {
                flashElement(priceEl, increased ? "flash-up" : "flash-down");
            }
        }

        const deltaValEl = document.getElementById("chart-delta-val");
        const deltaPctEl = document.getElementById("chart-delta-pct");
        const deltaContainer = document.getElementById("chart-price-delta");
        const isUp = newChange >= 0;
        const sign = isUp ? "+" : "";

        if (deltaValEl) deltaValEl.textContent = `${sign}${formatINR(newChange)}`;
        if (deltaPctEl) deltaPctEl.textContent = `(${sign}${newPct.toFixed(2)}%)`;
        if (deltaContainer) {
            deltaContainer.className = "stock-price-change " + (isUp ? "profit" : "loss");
        }

        if (chartCachedData && state.currentChartStock.timeframe === "1D") {
            chartCachedData.currentPrice = newPrice;
            chartCachedData.priceChange = newChange;
            chartCachedData.priceChangePercentage = newPct;

            if (chartCachedData.points && chartCachedData.points.length > 0) {
                // Update latest point on curve
                chartCachedData.points[chartCachedData.points.length - 1].price = newPrice;
            }

            renderStockCanvas(chartCachedData);
        }
    }

    window.triggerChartTrade = function (action) {
        if (!state.currentChartStock) return;
        const stock = state.currentChartStock;
        const price = chartCachedData?.currentPrice || 100;
        window.openFastTrade(stock.ipoId, stock.companyName, stock.symbol, price, price, 0, null, action);
    };

    window.triggerSimulatedSip = function () {
        if (!state.currentChartStock) return;
        alert(`Simulated SIP (Systematic Investment Plan)\n\nStock: ${state.currentChartStock.symbol} (${state.currentChartStock.companyName})\n\nAutomate simulated dollar-cost averaging every month from your virtual trading wallet.`);
    };

    window.toggleCurrentChartWatchlist = function () {
        if (!state.currentChartStock) return;
        const bookmark = document.getElementById("chart-bookmark-btn");
        if (bookmark) {
            bookmark.classList.toggle("text-warning");
            alert(`Added ${state.currentChartStock.symbol} to your personal watchlist!`);
        }
    };

    function populateNewsTab() {
        const container = document.getElementById("stock-news-container");
        if (!container || !state.currentChartStock) return;
        const sym = state.currentChartStock.symbol;
        container.innerHTML = `
            <div class="news-card-item">
                <h5>${sym} reports steady volume expansion in regular trading session</h5>
                <p>Institutional interest and high retail demand reflect optimistic outlook post recent market expansion.</p>
                <small class="text-muted">Today &middot; Live Market News</small>
            </div>
            <div class="news-card-item">
                <h5>Dual-Exchange quotes reflect strong liquidity across NSE and BSE</h5>
                <p>Spreads remain within standard tight band with steady depth across top order book tiers.</p>
                <small class="text-muted">Yesterday &middot; Exchange Bulletin</small>
            </div>
        `;
    }

    function populateEventsTab() {
        const container = document.getElementById("stock-events-container");
        if (!container || !state.currentChartStock) return;
        const sym = state.currentChartStock.symbol;
        container.innerHTML = `
            <div class="news-card-item">
                <h5>Board Meeting &middot; Financial Results</h5>
                <p>Scheduled audit and approval of upcoming quarterly performance figures and dividend review.</p>
                <small class="text-success">Upcoming &middot; 28 Nov 2026</small>
            </div>
            <div class="news-card-item">
                <h5>Annual General Meeting (AGM)</h5>
                <p>Discussion on future roadmap, capital expenditure, and investor governance updates.</p>
                <small class="text-muted">Completed &middot; Previous Month</small>
            </div>
        `;
    }

})();
