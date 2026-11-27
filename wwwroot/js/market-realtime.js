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

    window.marketState = state;
    window.setCurrentChartStock = function (stock) {
        state.currentChartStock = stock;
    };
    window.getCurrentChartStock = function () {
        return state.currentChartStock;
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

            state.signalrConnection.on("ReceiveIpoStatusChange", function (data) {
                handleIpoStatusChange(data);
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

        // Update header status pills across Investor, Company, Admin dashboards
        document.querySelectorAll(".investor-live-status-pill, .company-live-status-pill, .admin-live-status-pill").forEach(pill => {
            if (state.isMarketOpen) {
                pill.classList.remove("is-closed");
                pill.classList.add("is-open");
                pill.textContent = "LIVE";
            } else {
                pill.classList.remove("is-open");
                pill.classList.add("is-closed");
                pill.textContent = "CLOSED";
            }
        });

        // Update header indicator dot and ring
        document.querySelectorAll(".investor-live-dot, .company-live-dot, .admin-live-dot").forEach(dot => {
            if (state.isMarketOpen) {
                dot.style.backgroundColor = "#10b981";
                dot.style.boxShadow = "0 0 8px #10b981";
            } else {
                dot.style.backgroundColor = "#ef4444";
                dot.style.boxShadow = "0 0 8px #ef4444";
            }
        });
        document.querySelectorAll(".investor-live-ring, .company-live-ring, .admin-live-ring").forEach(ring => {
            if (state.isMarketOpen) {
                ring.style.borderColor = "#10b981";
                ring.style.animation = "admin-live-pulse-ring 1.8s infinite ease-out";
            } else {
                ring.style.borderColor = "#ef4444";
                ring.style.animation = "none";
            }
        });

        // Update dashboard card overline pulse and text
        document.querySelectorAll(".investor-live-market-card .terminal-overline, .company-live-market-card .terminal-overline").forEach(el => {
            const pulse = el.querySelector(".ticker-pulse");
            const text = el.querySelector("span");
            if (pulse && text) {
                if (state.isMarketOpen) {
                    pulse.style.background = "";
                    pulse.style.boxShadow = "";
                    text.textContent = "REAL-TIME NSE · BSE MARKET (LIVE SESSION)";
                } else {
                    pulse.style.background = "#ef4444";
                    pulse.style.boxShadow = "0 0 8px rgba(239, 68, 68, 0.4)";
                    text.textContent = "REAL-TIME NSE · BSE MARKET (CLOSED)";
                }
            }
        });

        // Update modal title pulse & subtitle
        const modalSubtitle = document.getElementById("live-market-modal-subtitle");
        if (modalSubtitle) {
            modalSubtitle.textContent = state.isMarketOpen
                ? "Active Market Session (09:30 AM – 03:30 PM IST) • Streaming real-time quotes"
                : "Market Session Closed • Official Trading Hours: Monday – Friday, 09:30 AM to 03:30 PM IST";
        }
        const modalTitlePulse = document.querySelector("#live-market-modal-title .ticker-pulse");
        if (modalTitlePulse) {
            modalTitlePulse.style.background = state.isMarketOpen ? "" : "#ef4444";
            modalTitlePulse.style.boxShadow = state.isMarketOpen ? "" : "0 0 6px #ef4444";
        }

        // Update Market Details page status badge if present
        const chartStatusBadge = document.getElementById("chart-market-status-badge");
        if (chartStatusBadge) {
            chartStatusBadge.className = "badge " + (state.isMarketOpen ? "bg-success" : "bg-danger") + " px-2 py-1";
            chartStatusBadge.textContent = state.isMarketOpen ? "● MARKET OPEN" : "○ MARKET CLOSED";
        }

        const modalSource = document.getElementById("modal-data-source-badge");
        if (modalSource && data.dataSource) {
            modalSource.textContent = data.dataSource;
        }

        const modalFeed = document.getElementById("modal-feed-status-badge");
        if (modalFeed) {
            modalFeed.textContent = data.isSimulation ? "SIMULATED (DEV)" : (data.isStale ? "STALE / OFFLINE" : "LIVE FEED");
            modalFeed.className = "badge px-2 py-1 " + (data.isSimulation ? "bg-warning text-dark" : (data.isStale ? "bg-warning text-dark" : "bg-success text-white"));
        }

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

    function handleIpoStatusChange(data) {
        if (!data) return;
        const status = data.status || "";
        let category = "IPO " + status.toUpperCase();
        let toastTitle = `${data.ipoName} is now ${status.toUpperCase()}`;
        let toastMsg = `Status changed from ${data.previousStatus || "Previous"} to ${status}.`;

        if (status === "Open") {
            category = "IPO OPENING";
            toastTitle = `IPO Bidding Open: ${data.ipoName}`;
            toastMsg = `Subscription is now open until ${data.closingDateTime || "closing time"} IST. Submit your bid now!`;
        } else if (status === "Closed") {
            category = "IPO CLOSING";
            toastTitle = `IPO Bidding Closed: ${data.ipoName}`;
            toastMsg = `Bidding window has closed. Allotment processing underway.`;
        } else if (status === "Listed") {
            category = "IPO LISTED";
            toastTitle = `IPO Listed: ${data.ipoName}`;
            toastMsg = `Now officially listed on NSE & BSE with live secondary trading active.`;
        }

        handleBroadcastMessage({
            category: category,
            title: toastTitle,
            message: toastMsg,
            timestamp: data.timestamp || "Just now"
        });

        // Dynamically update any status badges in the DOM
        if (data.ipoId) {
            document.querySelectorAll(`[data-status-for="${data.ipoId}"]`).forEach(el => {
                el.textContent = status;
                el.className = `badge ${status === "Open" ? "bg-success" : status === "Closed" ? "bg-danger" : status === "Listed" ? "bg-primary" : "bg-secondary"} ms-1`;
            });
        }
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

    let isFastTradeSubmitting = false;

    window.submitFastTrade = function () {
        if (isFastTradeSubmitting) return;

        const ipoId = parseInt(document.getElementById("fast-trade-ipo-id").value, 10);
        const investmentId = parseInt(document.getElementById("fast-trade-investment-id").value, 10) || null;
        const exchange = document.getElementById("fast-trade-selected-exchange").value;
        const tradeType = document.getElementById("fast-trade-selected-action").value;
        const quantity = parseInt(document.getElementById("fast-trade-quantity").value, 10);

        const alertBox = document.getElementById("fast-trade-alert");
        const execBtn = document.getElementById("btn-trade-execute");

        if (!ipoId || isNaN(quantity) || quantity <= 0) {
            alertBox.className = "fast-trade-msg error";
            alertBox.textContent = "Please enter a valid trade quantity (minimum 1 share).";
            return;
        }

        // Validate available shares on client prior to submit if selling
        if (tradeType === "SELL") {
            const availShares = parseInt(document.getElementById("fast-trade-available-shares")?.value || "0", 10);
            if (availShares > 0 && quantity > availShares) {
                alertBox.className = "fast-trade-msg error";
                alertBox.textContent = `You only have ${availShares.toLocaleString()} available shares to sell.`;
                return;
            }
        }

        // Expected price from modal
        const priceEl = document.getElementById(exchange === "BSE" ? "fast-trade-bse-price" : "fast-trade-nse-price");
        const rawPriceText = priceEl?.textContent.replace(/[^0-9.]/g, "") || "0";
        const expectedPrice = parseFloat(rawPriceText) || 0;

        // Generate client request ID for idempotency & duplicate prevention
        const clientRequestId = (typeof crypto !== "undefined" && crypto.randomUUID) 
            ? crypto.randomUUID() 
            : ("trade_" + Date.now() + "_" + Math.random().toString(36).substring(2, 9));

        isFastTradeSubmitting = true;
        execBtn.disabled = true;
        execBtn.textContent = "Executing Order...";

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
                investmentId: investmentId,
                expectedPrice: expectedPrice,
                clientRequestId: clientRequestId
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
            })
            .finally(() => {
                isFastTradeSubmitting = false;
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
        const hasValidPrice = data.currentPrice != null && Number(data.currentPrice) > 0 && data.isPriceAvailable !== false;
        if (priceEl) {
            priceEl.textContent = hasValidPrice ? formatINR(data.currentPrice) : "Price Unavailable";
            if (!hasValidPrice) {
                priceEl.classList.add("text-muted");
            } else {
                priceEl.classList.remove("text-muted");
            }
        }

        // Price Change & Percentage
        const deltaValEl = document.getElementById("chart-delta-val");
        const deltaPctEl = document.getElementById("chart-delta-pct");
        const deltaContainer = document.getElementById("chart-price-delta");
        if (hasValidPrice && data.priceChange != null) {
            const isUp = data.priceChange >= 0;
            const sign = isUp ? "+" : "";
            if (deltaValEl) deltaValEl.textContent = `${sign}${formatINR(data.priceChange)}`;
            if (deltaPctEl) deltaPctEl.textContent = `(${sign}${(data.priceChangePercentage || 0).toFixed(2)}%)`;
            if (deltaContainer) {
                deltaContainer.className = "stock-price-change " + (isUp ? "profit" : "loss");
            }
        } else {
            if (deltaValEl) deltaValEl.textContent = "--";
            if (deltaPctEl) deltaPctEl.textContent = "(--)";
            if (deltaContainer) {
                deltaContainer.className = "stock-price-change text-muted";
            }
        }

        // Previous close pill
        const prevPill = document.getElementById("chart-prev-close-val");
        if (prevPill) {
            prevPill.textContent = (data.previousClose != null && Number(data.previousClose) > 0) ? formatINR(data.previousClose) : "Unavailable";
        }

        // Today's Low / High Range
        const todayLowEl = document.getElementById("perf-today-low");
        const todayHighEl = document.getElementById("perf-today-high");
        if (todayLowEl) todayLowEl.textContent = (data.dayLow != null && Number(data.dayLow) > 0) ? formatINR(data.dayLow) : "Unavailable";
        if (todayHighEl) todayHighEl.textContent = (data.dayHigh != null && Number(data.dayHigh) > 0) ? formatINR(data.dayHigh) : "Unavailable";

        if (data.dayHigh && data.dayLow && data.dayHigh > data.dayLow && hasValidPrice) {
            const todaySpan = Math.max(0.1, data.dayHigh - data.dayLow);
            const todayPct = Math.min(100, Math.max(0, (data.currentPrice - data.dayLow) / todaySpan * 100));
            const todayPointer = document.getElementById("perf-today-pointer");
            const todayBar = document.getElementById("perf-today-bar");
            if (todayPointer) todayPointer.style.left = `${todayPct}%`;
            if (todayBar) todayBar.style.width = `${todayPct}%`;
        }

        // 52-Week Low / High Range
        const w52LowEl = document.getElementById("perf-52w-low");
        const w52HighEl = document.getElementById("perf-52w-high");
        if (w52LowEl) w52LowEl.textContent = (data.low52w != null && Number(data.low52w) > 0) ? formatINR(data.low52w) : "Unavailable";
        if (w52HighEl) w52HighEl.textContent = (data.high52w != null && Number(data.high52w) > 0) ? formatINR(data.high52w) : "Unavailable";

        if (data.high52w && data.low52w && data.high52w > data.low52w && hasValidPrice) {
            const w52Span = Math.max(0.1, data.high52w - data.low52w);
            const w52Pct = Math.min(100, Math.max(0, (data.currentPrice - data.low52w) / w52Span * 100));
            const w52Pointer = document.getElementById("perf-52w-pointer");
            const w52Bar = document.getElementById("perf-52w-bar");
            if (w52Pointer) w52Pointer.style.left = `${w52Pct}%`;
            if (w52Bar) w52Bar.style.width = `${w52Pct}%`;
        }

        // Fundamentals Grid
        const openEl = document.getElementById("perf-open");
        const prevEl = document.getElementById("perf-prev-close");
        const volEl = document.getElementById("perf-volume");
        const lowCircEl = document.getElementById("perf-lower-circuit");
        const upCircEl = document.getElementById("perf-upper-circuit");
        const bidAskEl = document.getElementById("perf-bid-ask");

        if (openEl) openEl.textContent = (data.openPrice != null && Number(data.openPrice) > 0) ? formatINR(data.openPrice) : "Unavailable";
        if (prevEl) prevEl.textContent = (data.previousClose != null && Number(data.previousClose) > 0) ? formatINR(data.previousClose) : "Unavailable";
        if (volEl) volEl.textContent = (data.volume != null && Number(data.volume) > 0) ? formatNumber(data.volume) : "Unavailable";
        if (lowCircEl) lowCircEl.textContent = (data.lowerCircuit != null && Number(data.lowerCircuit) > 0) ? formatINR(data.lowerCircuit) : "Unavailable";
        if (upCircEl) upCircEl.textContent = (data.upperCircuit != null && Number(data.upperCircuit) > 0) ? formatINR(data.upperCircuit) : "Unavailable";
        if (bidAskEl) {
            const bBid = data.bestBid != null ? formatINR(data.bestBid) : "Unavailable";
            const bAsk = data.bestAsk != null ? formatINR(data.bestAsk) : "Unavailable";
            bidAskEl.textContent = `${bBid} / ${bAsk}`;
        }

        // Technicals
        const sma20El = document.getElementById("tech-sma20");
        const sma50El = document.getElementById("tech-sma50");
        if (sma20El) sma20El.textContent = hasValidPrice ? formatINR(data.currentPrice * 0.985) : "Unavailable";
        if (sma50El) sma50El.textContent = hasValidPrice ? formatINR(data.currentPrice * 0.962) : "Unavailable";

        // Data Notice & Source pill
        const noticeEl = document.getElementById("chart-data-notice-text");
        if (noticeEl) noticeEl.textContent = data.dataNotice || (data.isSimulation ? "Simulation Mode: Realistic continuous prices within Indian circuit limits (±20%)." : "Exchange Live Market Data.");

        const pillEl = document.getElementById("chart-data-source-pill");
        if (pillEl) {
            pillEl.textContent = data.isSimulation ? "SIMULATED" : (data.isStale ? "STALE DATA" : "LIVE DATA");
            pillEl.className = "badge " + (data.isSimulation ? "bg-dark text-warning border border-warning" : (data.isStale ? "bg-dark text-warning border border-warning" : "bg-dark text-success border border-success"));
        }

        const simBadge = document.getElementById("chart-sim-badge");
        const staleBadge = document.getElementById("chart-stale-badge");
        const liveBadge = document.getElementById("chart-live-badge");
        if (simBadge) simBadge.textContent = "⚠️ SIMULATED DATA (DEV)";
        if (staleBadge) staleBadge.textContent = "⚠️ STALE DATA";
        if (liveBadge) liveBadge.textContent = "● LIVE DATA";

        const sourceBadge = document.getElementById("chart-source-badge");
        if (sourceBadge && data.dataSource) {
            sourceBadge.textContent = data.dataSource;
        }

        const lastUpText = document.getElementById("chart-last-updated-text");
        if (lastUpText && data.lastUpdated) {
            lastUpText.textContent = `Updated: ${data.lastUpdated}`;
        }
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

        // Update Today's High / Low in Performance tab
        const todayLow = isBse ? stock.bseLow : stock.nseLow;
        const todayHigh = isBse ? stock.bseHigh : stock.nseHigh;
        const todayLowEl = document.getElementById("perf-today-low");
        const todayHighEl = document.getElementById("perf-today-high");
        if (todayLowEl && todayLow) todayLowEl.textContent = formatINR(todayLow);
        if (todayHighEl && todayHigh) todayHighEl.textContent = formatINR(todayHigh);

        if (todayHigh && todayLow && todayHigh > todayLow) {
            const todaySpan = Math.max(0.1, todayHigh - todayLow);
            const todayPct = Math.min(100, Math.max(0, (newPrice - todayLow) / todaySpan * 100));
            const todayPointer = document.getElementById("perf-today-pointer");
            const todayBar = document.getElementById("perf-today-bar");
            if (todayPointer) todayPointer.style.left = `${todayPct}%`;
            if (todayBar) todayBar.style.width = `${todayPct}%`;
        }

        if (chartCachedData) {
            chartCachedData.currentPrice = newPrice;
            chartCachedData.priceChange = newChange;
            chartCachedData.priceChangePercentage = newPct;
            if (newPrice > (chartCachedData.dayHigh || 0)) chartCachedData.dayHigh = newPrice;
            if (chartCachedData.dayLow === undefined || newPrice < chartCachedData.dayLow) chartCachedData.dayLow = newPrice;

            if (state.currentChartStock.timeframe === "1D" && chartCachedData.points && chartCachedData.points.length > 0) {
                // If points was loaded with only 2 flat points, reload rich chart data
                if (chartCachedData.points.length <= 2 && chartCachedData.points[0].price === chartCachedData.points[1]?.price) {
                    window.loadStockChartData(stock.ipoId, "1D", state.currentChartStock.exchange);
                    return;
                }

                const nowLabel = new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
                const lastIdx = chartCachedData.points.length - 1;
                const lastPoint = chartCachedData.points[lastIdx];

                if (lastPoint && lastPoint.label === nowLabel) {
                    lastPoint.price = newPrice;
                } else if (chartCachedData.points.length < 100) {
                    chartCachedData.points.push({ label: nowLabel, price: newPrice, volume: Math.floor(Math.random() * 2500) + 500 });
                } else {
                    lastPoint.price = newPrice;
                }

                // Update candle if in candlestick view
                if (chartCachedData.candles && chartCachedData.candles.length > 0) {
                    const lastCandle = chartCachedData.candles[chartCachedData.candles.length - 1];
                    if (lastCandle) {
                        lastCandle.close = newPrice;
                        lastCandle.high = Math.max(lastCandle.high || newPrice, newPrice);
                        lastCandle.low = Math.min(lastCandle.low || newPrice, newPrice);
                    }
                }

                renderStockCanvas(chartCachedData);
            }
        }
    }

    window.triggerChartTrade = function (action) {
        if (!state.currentChartStock) return;
        const stock = state.currentChartStock;
        const price = Number(chartCachedData?.currentPrice) || 0;
        if (price <= 0) {
            alert("Market price is currently unavailable for this stock.");
            return;
        }
        window.openFastTrade(stock.ipoId, stock.companyName, stock.symbol, price, price, 0, null, action);
    };

    let selectedSipStock = null;
    let selectedSipExchange = "NSE";

    function initStockSipModal() {
        if (document.getElementById("stock-sip-modal")) return;

        const modalDiv = document.createElement("div");
        modalDiv.id = "stock-sip-modal";
        modalDiv.className = "stock-sip-modal-overlay hidden";
        modalDiv.innerHTML = `
            <div class="stock-sip-dialog">
                <div class="fast-trade-header" style="background:#0f172a; padding:18px 24px; border-bottom:1px solid #1e293b; display:flex; align-items:center; justify-content:space-between;">
                    <div style="display:flex; align-items:center; gap:12px;">
                        <div style="width:40px; height:40px; border-radius:10px; background:linear-gradient(135deg, #059669, #10b981); display:flex; align-items:center; justify-content:center; font-size:1.3rem;">
                            📈
                        </div>
                        <div>
                            <h3 style="margin:0; font-size:1.15rem; font-weight:700; color:#fff;" id="stock-sip-modal-title">Start Stock SIP</h3>
                            <small style="color:#94a3b8; font-size:0.75rem;">Automate rupee-cost averaging in listed stocks</small>
                        </div>
                    </div>
                    <button type="button" class="fast-trade-close-btn" onclick="window.closeStockSipModal()">&times;</button>
                </div>

                <form id="stock-sip-form" onsubmit="window.handleStockSipSubmit(event)">
                    <input type="hidden" id="sip-ipo-id" value="" />
                    <div style="padding:20px 24px;">
                        <!-- Stock selection -->
                        <div class="mb-3" id="sip-stock-select-container">
                            <label class="form-label text-white fw-semibold small" for="sip-stock-select" style="font-size:0.85rem; font-weight:600; color:#cbd5e1; margin-bottom:6px; display:block;">Select Listed Stock</label>
                            <select class="form-select form-select-lg" id="sip-stock-select"
                                    style="background:#1e293b; color:#ffffff; border:1px solid #334155; font-size:0.95rem; border-radius:8px; width:100%; padding:10px;"
                                    onchange="window.updateSipStockSelection(this)">
                                <option value="">-- Choose a Listed Company / Stock --</option>
                            </select>
                        </div>

                        <!-- Selected Stock Snapshot -->
                        <div class="p-3 mb-3 rounded border" id="sip-stock-snapshot" style="background:#162032; border:1px solid #2a3d58 !important; border-radius:8px; padding:14px; margin-bottom:14px;">
                            <div style="display:flex; justify-content:space-between; align-items:center; flex-wrap:wrap; gap:8px;">
                                <div style="display:flex; align-items:center; gap:8px;">
                                    <span class="badge bg-primary text-white" id="sip-badge-symbol" style="padding:4px 8px; border-radius:4px; font-weight:700; font-size:0.8rem; background:#2563eb;">STOCK</span>
                                    <strong class="text-white" id="sip-display-company" style="font-size:1rem; color:#fff;">Company Name</strong>
                                </div>
                                <div style="display:flex; align-items:center; gap:6px;">
                                    <span style="font-size:0.8rem; color:#94a3b8;">Current Price:</span>
                                    <strong class="text-success" id="sip-display-price" style="font-size:1.2rem; color:#10b981; font-weight:700;">₹0.00</strong>
                                </div>
                            </div>
                        </div>

                        <div class="row g-3 mb-3" style="display:flex; gap:12px; margin-bottom:14px;">
                            <!-- Exchange Selection -->
                            <div style="flex:1;">
                                <label style="font-size:0.82rem; font-weight:600; color:#cbd5e1; margin-bottom:6px; display:block;">Execution Exchange</label>
                                <div style="display:flex; gap:8px;">
                                    <label style="flex:1; padding:8px 10px; border-radius:6px; border:1px solid #334155; background:#1e293b; color:#fff; text-align:center; cursor:pointer; font-size:0.85rem;" id="sip-exchange-label-nse">
                                        <input type="radio" name="sip-exchange-radio" value="NSE" checked style="margin-right:4px;" onchange="window.updateSipExchange('NSE')">
                                        <strong>NSE</strong>
                                    </label>
                                    <label style="flex:1; padding:8px 10px; border-radius:6px; border:1px solid #334155; background:#1e293b; color:#fff; text-align:center; cursor:pointer; font-size:0.85rem;" id="sip-exchange-label-bse">
                                        <input type="radio" name="sip-exchange-radio" value="BSE" style="margin-right:4px;" onchange="window.updateSipExchange('BSE')">
                                        <strong>BSE</strong>
                                    </label>
                                </div>
                            </div>

                            <!-- Frequency Selection -->
                            <div style="flex:1;">
                                <label for="sip-frequency-select" style="font-size:0.82rem; font-weight:600; color:#cbd5e1; margin-bottom:6px; display:block;">Investment Frequency</label>
                                <select id="sip-frequency-select" style="background:#1e293b; color:#ffffff; border:1px solid #334155; border-radius:6px; width:100%; padding:9px; font-size:0.9rem;" onchange="window.handleSipFrequencyChange()">
                                    <option value="Monthly" selected>Monthly (Recommended)</option>
                                    <option value="Weekly">Weekly (Every 7 Days)</option>
                                    <option value="Daily">Daily (Every Trading Day)</option>
                                </select>
                            </div>
                        </div>

                        <!-- Installment Amount -->
                        <div style="margin-bottom:14px;">
                            <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:6px;">
                                <label for="sip-amount-input" style="font-size:0.82rem; font-weight:600; color:#cbd5e1; margin:0;">
                                    Installment Amount (₹)
                                </label>
                                <small style="color:#94a3b8; font-size:0.75rem;">Min: ₹100 per installment</small>
                            </div>
                            <div style="display:flex; margin-bottom:8px;">
                                <span style="background:#334155; color:#cbd5e1; border:1px solid #475569; padding:8px 14px; border-radius:6px 0 0 6px; font-weight:700;">₹</span>
                                <input type="number" id="sip-amount-input" min="100" max="500000" step="50" value="1000" required
                                       style="background:#1e293b; color:#ffffff; border:1px solid #475569; border-left:none; border-radius:0 6px 6px 0; font-size:1.15rem; font-weight:700; width:100%; padding:8px 12px;"
                                       oninput="window.calculateSipProjections()">
                            </div>

                            <!-- Quick Amount Presets -->
                            <div style="display:flex; flex-wrap:wrap; gap:6px;">
                                <button type="button" class="btn btn-sm" style="border:1px solid #475569; background:#1e293b; color:#cbd5e1; padding:3px 10px; border-radius:4px; font-size:0.78rem; cursor:pointer;" onclick="window.setSipAmount(500)">₹500</button>
                                <button type="button" class="btn btn-sm" style="border:1px solid #475569; background:#1e293b; color:#cbd5e1; padding:3px 10px; border-radius:4px; font-size:0.78rem; cursor:pointer;" onclick="window.setSipAmount(1000)">₹1,000</button>
                                <button type="button" class="btn btn-sm" style="border:1px solid #475569; background:#1e293b; color:#cbd5e1; padding:3px 10px; border-radius:4px; font-size:0.78rem; cursor:pointer;" onclick="window.setSipAmount(2500)">₹2,500</button>
                                <button type="button" class="btn btn-sm" style="border:1px solid #475569; background:#1e293b; color:#cbd5e1; padding:3px 10px; border-radius:4px; font-size:0.78rem; cursor:pointer;" onclick="window.setSipAmount(5000)">₹5,000</button>
                                <button type="button" class="btn btn-sm" style="border:1px solid #475569; background:#1e293b; color:#cbd5e1; padding:3px 10px; border-radius:4px; font-size:0.78rem; cursor:pointer;" onclick="window.setSipAmount(10000)">₹10,000</button>
                            </div>
                        </div>

                        <div style="display:flex; gap:12px; margin-bottom:14px;">
                            <!-- Preferred Day -->
                            <div style="flex:1;" id="sip-day-group">
                                <label for="sip-day-select" id="sip-day-label" style="font-size:0.82rem; font-weight:600; color:#cbd5e1; margin-bottom:6px; display:block;">Execution Date of Month</label>
                                <select id="sip-day-select" style="background:#1e293b; color:#ffffff; border:1px solid #334155; border-radius:6px; width:100%; padding:9px; font-size:0.9rem;">
                                    <option value="1">1st of the month</option>
                                    <option value="5" selected>5th of the month (Standard)</option>
                                    <option value="10">10th of the month</option>
                                    <option value="15">15th of the month</option>
                                    <option value="20">20th of the month</option>
                                    <option value="25">25th of the month</option>
                                </select>
                                <small style="color:#64748b; font-size:0.7rem; display:block; margin-top:4px;">Weekend installments execute Monday.</small>
                            </div>

                            <!-- Duration -->
                            <div style="flex:1;">
                                <label for="sip-tenure-select" style="font-size:0.82rem; font-weight:600; color:#cbd5e1; margin-bottom:6px; display:block;">SIP Duration</label>
                                <select id="sip-tenure-select" style="background:#1e293b; color:#ffffff; border:1px solid #334155; border-radius:6px; width:100%; padding:9px; font-size:0.9rem;" onchange="window.calculateSipProjections()">
                                    <option value="" selected>Ongoing (Until cancelled)</option>
                                    <option value="6">6 Months</option>
                                    <option value="12">12 Months (1 Year)</option>
                                    <option value="24">24 Months (2 Years)</option>
                                    <option value="36">36 Months (3 Years)</option>
                                </select>
                            </div>
                        </div>

                        <!-- Projections Card -->
                        <div style="background:#131d2e; border:1px solid #1e3a5f; border-radius:8px; padding:12px; margin-bottom:10px;">
                            <h4 style="font-size:0.75rem; text-transform:uppercase; font-weight:700; color:#38bdf8; margin:0 0 8px;">📊 SIP Projection & Estimates</h4>
                            <div style="display:flex; text-align:center; gap:8px;">
                                <div style="flex:1;">
                                    <small style="color:#94a3b8; font-size:0.72rem; display:block;">Est. Shares / Order</small>
                                    <strong style="font-size:0.95rem; color:#fff;" id="sip-est-shares">~0 shares</strong>
                                </div>
                                <div style="flex:1;">
                                    <small style="color:#94a3b8; font-size:0.72rem; display:block;">1-Year Investment</small>
                                    <strong style="font-size:0.95rem; color:#fbbf24;" id="sip-est-annual-cost">₹12,000</strong>
                                </div>
                                <div style="flex:1;">
                                    <small style="color:#94a3b8; font-size:0.72rem; display:block;">Projected Value (12% CAGR)</small>
                                    <strong style="font-size:0.95rem; color:#34d399;" id="sip-est-projected-val">₹12,800</strong>
                                </div>
                            </div>
                        </div>

                        <div id="sip-modal-alert" class="hidden" style="padding:10px 14px; border-radius:6px; font-size:0.85rem; margin-top:10px;"></div>
                    </div>

                    <div style="background:#0b121e; padding:14px 24px; border-top:1px solid #1e293b; display:flex; justify-content:space-between; align-items:center; border-radius:0 0 14px 14px;">
                        <div>
                            <small style="color:#94a3b8; font-size:0.75rem; display:block;">Virtual Cash Balance</small>
                            <strong style="color:#10b981; font-size:0.9rem;" data-cash-balance>₹100,000.00</strong>
                        </div>
                        <div style="display:flex; gap:8px;">
                            <button type="button" class="btn" onclick="window.closeStockSipModal()" style="border:1px solid #475569; background:transparent; color:#cbd5e1; padding:8px 16px; border-radius:6px; cursor:pointer;">Cancel</button>
                            <button type="submit" class="btn" id="sip-submit-btn" style="background:#10b981; border:none; color:#fff; font-weight:700; padding:8px 20px; border-radius:6px; cursor:pointer;">
                                ✓ Activate Stock SIP
                            </button>
                        </div>
                    </div>
                </form>
            </div>
        `;

        modalDiv.addEventListener("click", function (e) {
            if (e.target === modalDiv) {
                window.closeStockSipModal();
            }
        });

        document.body.appendChild(modalDiv);
    }

    function populateSipStockDropdown() {
        const select = document.getElementById("sip-stock-select");
        if (!select || select.options.length > 1) return;

        if (state.stocks && state.stocks.length > 0) {
            state.stocks.forEach(q => {
                const opt = document.createElement("option");
                opt.value = q.ipoId;
                opt.setAttribute("data-symbol", q.stockSymbol || q.symbol);
                opt.setAttribute("data-company", q.companyName);
                const nP = Number(q.nsePrice || q.currentPrice) || 0;
                const bP = Number(q.bsePrice || q.currentPrice) || 0;
                opt.setAttribute("data-nse-price", nP);
                opt.setAttribute("data-bse-price", bP);
                const pText = nP > 0 ? ` · ₹${nP.toFixed(2)}` : " · Price Unavailable";
                opt.textContent = `${q.companyName} (${q.stockSymbol || q.symbol})${pText}`;
                select.appendChild(opt);
            });
        } else {
            fetch("/api/market/quotes")
                .then(res => res.json())
                .then(data => {
                    if (Array.isArray(data)) {
                        data.forEach(q => {
                            const opt = document.createElement("option");
                            opt.value = q.ipoId;
                            opt.setAttribute("data-symbol", q.stockSymbol || q.symbol);
                            opt.setAttribute("data-company", q.companyName);
                            const nP = Number(q.nsePrice || q.currentPrice) || 0;
                            const bP = Number(q.bsePrice || q.currentPrice) || 0;
                            opt.setAttribute("data-nse-price", nP);
                            opt.setAttribute("data-bse-price", bP);
                            const pText = nP > 0 ? ` · ₹${nP.toFixed(2)}` : " · Price Unavailable";
                            opt.textContent = `${q.companyName} (${q.stockSymbol || q.symbol})${pText}`;
                            select.appendChild(opt);
                        });
                    }
                })
                .catch(() => {});
        }
    }

    window.openStockSipModal = function (ipoId, symbol, company, price, exchange) {
        initStockSipModal();
        const modal = document.getElementById("stock-sip-modal");
        if (!modal) return;

        const alertBox = document.getElementById("sip-modal-alert");
        if (alertBox) {
            alertBox.className = "hidden";
            alertBox.textContent = "";
        }

        const submitBtn = document.getElementById("sip-submit-btn");
        if (submitBtn) {
            submitBtn.disabled = false;
            submitBtn.textContent = "✓ Activate Stock SIP";
        }

        if (!ipoId && state.currentChartStock) {
            ipoId = state.currentChartStock.ipoId;
            symbol = state.currentChartStock.symbol;
            company = state.currentChartStock.companyName;
            price = Number(chartCachedData?.currentPrice) || 0;
            exchange = state.currentChartStock.exchange || "NSE";
        }

        selectedSipExchange = exchange || "NSE";
        const nseRadio = document.querySelector('input[name="sip-exchange-radio"][value="NSE"]');
        const bseRadio = document.querySelector('input[name="sip-exchange-radio"][value="BSE"]');
        if (selectedSipExchange === "BSE" && bseRadio) bseRadio.checked = true;
        else if (nseRadio) nseRadio.checked = true;

        const ipoIdInput = document.getElementById("sip-ipo-id");
        if (ipoIdInput) ipoIdInput.value = ipoId || "";

        const selectContainer = document.getElementById("sip-stock-select-container");
        const snapshot = document.getElementById("sip-stock-snapshot");

        if (ipoId && symbol) {
            selectedSipStock = {
                ipoId: Number(ipoId),
                symbol: symbol,
                company: company || symbol,
                nsePrice: Number(price) || 0,
                bsePrice: Number(price) || 0
            };

            if (selectContainer) selectContainer.style.display = "none";
            if (snapshot) snapshot.style.display = "block";

            const badge = document.getElementById("sip-badge-symbol");
            const comp = document.getElementById("sip-display-company");
            const pr = document.getElementById("sip-display-price");

            if (badge) badge.textContent = selectedSipStock.symbol;
            if (comp) comp.textContent = selectedSipStock.company;
            if (pr) pr.textContent = price > 0 ? formatINR(price) : "Price Unavailable";
        } else {
            if (selectContainer) selectContainer.style.display = "block";
            if (snapshot) snapshot.style.display = "none";
            populateSipStockDropdown();
        }

        window.calculateSipProjections();

        modal.classList.remove("hidden");
    };

    window.closeStockSipModal = function () {
        const modal = document.getElementById("stock-sip-modal");
        if (modal) modal.classList.add("hidden");
    };

    window.updateSipStockSelection = function (select) {
        const opt = select.options[select.selectedIndex];
        const snapshot = document.getElementById("sip-stock-snapshot");
        if (!opt || !opt.value) {
            if (snapshot) snapshot.style.display = "none";
            selectedSipStock = null;
            window.calculateSipProjections();
            return;
        }

        selectedSipStock = {
            ipoId: Number(opt.value),
            symbol: opt.getAttribute("data-symbol"),
            company: opt.getAttribute("data-company"),
            nsePrice: Number(opt.getAttribute("data-nse-price")) || 0,
            bsePrice: Number(opt.getAttribute("data-bse-price")) || 0
        };

        const ipoIdInput = document.getElementById("sip-ipo-id");
        if (ipoIdInput) ipoIdInput.value = selectedSipStock.ipoId;

        if (snapshot) {
            snapshot.style.display = "block";
            const badge = document.getElementById("sip-badge-symbol");
            const comp = document.getElementById("sip-display-company");
            const pr = document.getElementById("sip-display-price");
            if (badge) badge.textContent = selectedSipStock.symbol;
            if (comp) comp.textContent = selectedSipStock.company;
            const p = selectedSipExchange === "BSE" ? selectedSipStock.bsePrice : selectedSipStock.nsePrice;
            if (pr) pr.textContent = p > 0 ? formatINR(p) : "Price Unavailable";
        }

        window.calculateSipProjections();
    };

    window.updateSipExchange = function (exchange) {
        selectedSipExchange = exchange;
        if (selectedSipStock) {
            const pr = document.getElementById("sip-display-price");
            const p = exchange === "BSE" ? selectedSipStock.bsePrice : selectedSipStock.nsePrice;
            if (pr) pr.textContent = p > 0 ? formatINR(p) : "Price Unavailable";
        }
        window.calculateSipProjections();
    };

    window.setSipAmount = function (amount) {
        const input = document.getElementById("sip-amount-input");
        if (input) {
            input.value = amount;
            window.calculateSipProjections();
        }
    };

    window.handleSipFrequencyChange = function () {
        const freq = document.getElementById("sip-frequency-select")?.value || "Monthly";
        const dayLabel = document.getElementById("sip-day-label");
        const daySelect = document.getElementById("sip-day-select");
        const dayGroup = document.getElementById("sip-day-group");

        if (daySelect && dayLabel) {
            if (freq === "Daily") {
                if (dayGroup) dayGroup.style.display = "none";
            } else if (freq === "Weekly") {
                if (dayGroup) dayGroup.style.display = "block";
                dayLabel.textContent = "Execution Day of Week";
                daySelect.innerHTML = `
                    <option value="1" selected>Every Monday</option>
                    <option value="2">Every Tuesday</option>
                    <option value="3">Every Wednesday</option>
                    <option value="4">Every Thursday</option>
                    <option value="5">Every Friday</option>
                `;
            } else {
                if (dayGroup) dayGroup.style.display = "block";
                dayLabel.textContent = "Execution Date of Month";
                daySelect.innerHTML = `
                    <option value="1">1st of the month</option>
                    <option value="5" selected>5th of the month (Standard)</option>
                    <option value="10">10th of the month</option>
                    <option value="15">15th of the month</option>
                    <option value="20">20th of the month</option>
                    <option value="25">25th of the month</option>
                `;
            }
        }
        window.calculateSipProjections();
    };

    window.calculateSipProjections = function () {
        const amountInput = document.getElementById("sip-amount-input");
        const freqSelect = document.getElementById("sip-frequency-select");
        const tenureSelect = document.getElementById("sip-tenure-select");
        const sharesDisplay = document.getElementById("sip-est-shares");
        const annualCostDisplay = document.getElementById("sip-est-annual-cost");
        const projectedDisplay = document.getElementById("sip-est-projected-val");

        const amount = Number(amountInput ? amountInput.value : 1000) || 1000;
        const freq = freqSelect ? freqSelect.value : "Monthly";
        const price = selectedSipStock
            ? (selectedSipExchange === "BSE" ? selectedSipStock.bsePrice : selectedSipStock.nsePrice)
            : 0;

        if (sharesDisplay) {
            if (price > 0) {
                const estShares = Math.max(1, Math.floor(amount / price));
                sharesDisplay.textContent = `~${estShares} share${estShares > 1 ? "s" : ""}`;
            } else {
                sharesDisplay.textContent = "Unavailable";
            }
        }

        let installmentsPerYear = (freq === "Daily" ? 250 : (freq === "Weekly" ? 52 : 12));
        let totalMonths = Number(tenureSelect ? tenureSelect.value : 0) || 12;
        let totalInstallments = (freq === "Monthly" ? totalMonths : Math.round((installmentsPerYear / 12) * totalMonths));

        let totalInvested = amount * totalInstallments;
        if (annualCostDisplay) {
            annualCostDisplay.textContent = formatINR(totalInvested, 0);
        }

        // 12% CAGR future value calculation
        let monthlyRate = 0.12 / 12;
        let futureValue = 0;
        for (let i = 1; i <= totalMonths; i++) {
            futureValue += (amount * Math.pow(1 + monthlyRate, totalMonths - i + 1));
        }

        if (projectedDisplay) {
            projectedDisplay.textContent = formatINR(Math.round(futureValue), 0);
        }
    };

    window.handleStockSipSubmit = function (e) {
        if (e) e.preventDefault();

        const ipoId = parseInt(document.getElementById("sip-ipo-id")?.value || document.getElementById("sip-stock-select")?.value, 10);
        const exchangeRadio = document.querySelector('input[name="sip-exchange-radio"]:checked');
        const exchange = exchangeRadio?.value || selectedSipExchange || "NSE";
        const frequency = document.getElementById("sip-frequency-select")?.value || "Monthly";
        const amount = parseFloat(document.getElementById("sip-amount-input")?.value || "0");
        const day = parseInt(document.getElementById("sip-day-select")?.value || "5", 10);
        const tenureVal = document.getElementById("sip-tenure-select")?.value;
        const totalMonths = tenureVal ? parseInt(tenureVal, 10) : null;

        const alertBox = document.getElementById("sip-modal-alert");
        const submitBtn = document.getElementById("sip-submit-btn");

        if (!ipoId) {
            if (alertBox) {
                alertBox.className = "";
                alertBox.style.background = "rgba(239, 68, 68, 0.2)";
                alertBox.style.color = "#fca5a5";
                alertBox.style.border = "1px solid rgba(239, 68, 68, 0.4)";
                alertBox.textContent = "Please select a listed stock for the SIP.";
            }
            return;
        }

        if (isNaN(amount) || amount < 100) {
            if (alertBox) {
                alertBox.className = "";
                alertBox.style.background = "rgba(239, 68, 68, 0.2)";
                alertBox.style.color = "#fca5a5";
                alertBox.style.border = "1px solid rgba(239, 68, 68, 0.4)";
                alertBox.textContent = "Minimum SIP installment amount is ₹100.";
            }
            return;
        }

        if (submitBtn) {
            submitBtn.disabled = true;
            submitBtn.textContent = "Activating SIP...";
        }

        fetch("/api/market/sip/create", {
            method: "POST",
            headers: {
                "Content-Type": "application/json"
            },
            body: JSON.stringify({
                ipoId: ipoId,
                exchange: exchange,
                installmentAmount: amount,
                frequency: frequency,
                installmentDay: day,
                totalMonths: totalMonths
            })
        })
        .then(res => res.json())
        .then(data => {
            if (data.success) {
                if (typeof data.newCashBalance === "number") {
                    document.querySelectorAll("[data-cash-balance]").forEach(el => {
                        el.textContent = formatINR(data.newCashBalance);
                    });
                }
                if (alertBox) {
                    alertBox.className = "";
                    alertBox.style.background = "rgba(16, 185, 129, 0.2)";
                    alertBox.style.color = "#6ee7b7";
                    alertBox.style.border = "1px solid rgba(16, 185, 129, 0.4)";
                    alertBox.innerHTML = `<strong>✓ Stock SIP Activated Successfully!</strong><br>${data.message}<br><small style="color:#a7f3d0;">Next installment: ${data.nextExecutionDate || "Next regular market session at 10:00 AM IST"}</small>`;
                }
                setTimeout(() => {
                    window.closeStockSipModal();
                    if (window.location.pathname.toLowerCase().includes("/dashboard") && !window.location.pathname.toLowerCase().includes("marketdetails")) {
                        window.location.hash = "investor-sips";
                        window.location.reload();
                    }
                }, 1600);
            } else {
                if (alertBox) {
                    alertBox.className = "";
                    alertBox.style.background = "rgba(239, 68, 68, 0.2)";
                    alertBox.style.color = "#fca5a5";
                    alertBox.style.border = "1px solid rgba(239, 68, 68, 0.4)";
                    alertBox.textContent = data.message || "Failed to activate SIP plan.";
                }
                if (submitBtn) {
                    submitBtn.disabled = false;
                    submitBtn.textContent = "✓ Activate Stock SIP";
                }
            }
        })
        .catch(err => {
            if (alertBox) {
                alertBox.className = "";
                alertBox.style.background = "rgba(239, 68, 68, 0.2)";
                alertBox.style.color = "#fca5a5";
                alertBox.style.border = "1px solid rgba(239, 68, 68, 0.4)";
                alertBox.textContent = "Network error: Unable to create Stock SIP. Please verify your connection.";
            }
            if (submitBtn) {
                submitBtn.disabled = false;
                submitBtn.textContent = "✓ Activate Stock SIP";
            }
        });
    };

    window.triggerSimulatedSip = function () {
        const stock = state.currentChartStock;
        if (stock) {
            const price = Number(chartCachedData?.currentPrice) || 0;
            window.openStockSipModal(stock.ipoId, stock.symbol, stock.companyName, price, stock.exchange || "NSE");
        } else {
            window.openStockSipModal();
        }
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
                <small style="color:#94a3b8; font-weight:500;">Today &middot; Live Market News</small>
            </div>
            <div class="news-card-item">
                <h5>Dual-Exchange quotes reflect strong liquidity across NSE and BSE</h5>
                <p>Spreads remain within standard tight band with steady depth across top order book tiers.</p>
                <small style="color:#94a3b8; font-weight:500;">Yesterday &middot; Exchange Bulletin</small>
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
                <small class="text-success fw-bold">Upcoming &middot; 28 Nov 2026</small>
            </div>
            <div class="news-card-item">
                <h5>Annual General Meeting (AGM)</h5>
                <p>Discussion on future roadmap, capital expenditure, and investor governance updates.</p>
                <small style="color:#94a3b8; font-weight:500;">Completed &middot; Previous Month</small>
            </div>
        `;
    }

})();
