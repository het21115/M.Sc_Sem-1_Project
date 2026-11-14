/**
 * GMP Tracker & Interactive Historical Chart
 * Unofficial Grey Market Premium Visualization
 */
(function () {
    "use strict";

    window.initGmpHistoricalChart = function (canvasId, tooltipId, data) {
        const canvas = document.getElementById(canvasId);
        const tooltip = document.getElementById(tooltipId);
        if (!canvas) return;

        if (!data || data.length === 0) {
            const ctx = canvas.getContext("2d");
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            ctx.font = "14px -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif";
            ctx.fillStyle = "#888";
            ctx.textAlign = "center";
            ctx.textBaseline = "middle";
            ctx.fillText("No unofficial GMP history recorded yet.", canvas.width / 2, canvas.height / 2);
            return;
        }

        let hoveredPoint = null;

        function render() {
            const rect = canvas.getBoundingClientRect();
            if (rect.width === 0 || rect.height === 0) return;

            const dpr = window.devicePixelRatio || 1;
            canvas.width = rect.width * dpr;
            canvas.height = rect.height * dpr;

            const ctx = canvas.getContext("2d");
            ctx.scale(dpr, dpr);

            const w = rect.width;
            const h = rect.height;

            ctx.clearRect(0, 0, w, h);

            const paddingLeft = 55;
            const paddingRight = 35;
            const paddingTop = 28;
            const paddingBottom = 32;

            const plotW = w - paddingLeft - paddingRight;
            const plotH = h - paddingTop - paddingBottom;

            // Extract values
            const values = data.map(d => Number(d.gmp) || 0);
            let minVal = Math.min(...values);
            let maxVal = Math.max(...values);

            if (minVal === maxVal) {
                minVal = Math.max(0, minVal - 10);
                maxVal = maxVal + 10;
            } else {
                const range = maxVal - minVal;
                minVal = Math.max(0, minVal - range * 0.15);
                maxVal = maxVal + range * 0.15;
            }

            const valRange = maxVal - minVal || 1;

            // Compute Coordinates
            const points = data.map((d, i) => {
                const x = data.length > 1
                    ? paddingLeft + (i / (data.length - 1)) * plotW
                    : paddingLeft + plotW / 2;
                const y = paddingTop + plotH - ((Number(d.gmp) - minVal) / valRange) * plotH;
                return { x, y, data: d };
            });

            // Grid lines (3 horizontal lines)
            ctx.strokeStyle = "#e5e9f0";
            ctx.lineWidth = 1;
            ctx.setLineDash([3, 3]);
            ctx.fillStyle = "#8a9ba8";
            ctx.font = "11px -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif";
            ctx.textAlign = "right";
            ctx.textBaseline = "middle";

            const gridSteps = 3;
            for (let step = 0; step <= gridSteps; step++) {
                const val = minVal + (step / gridSteps) * (maxVal - minVal);
                const gy = paddingTop + plotH - (step / gridSteps) * plotH;

                ctx.beginPath();
                ctx.moveTo(paddingLeft, gy);
                ctx.lineTo(w - paddingRight, gy);
                ctx.stroke();

                ctx.fillText("₹" + val.toFixed(0), paddingLeft - 8, gy);
            }
            ctx.setLineDash([]);

            // X-Axis Date Labels
            ctx.textAlign = "center";
            ctx.textBaseline = "top";
            const maxLabels = Math.min(data.length, Math.floor(plotW / 65));
            const step = Math.max(1, Math.ceil(data.length / maxLabels));

            data.forEach((d, i) => {
                if (i % step === 0 || i === data.length - 1) {
                    const pt = points[i];
                    ctx.fillText(d.date, pt.x, h - paddingBottom + 8);
                }
            });

            const isPositive = (data[data.length - 1].gmp || 0) >= 0;
            const strokeColor = isPositive ? "#10b981" : "#ef4444";
            const fillColorTop = isPositive ? "rgba(16, 185, 129, 0.22)" : "rgba(239, 68, 68, 0.22)";
            const fillColorBottom = isPositive ? "rgba(16, 185, 129, 0.0)" : "rgba(239, 68, 68, 0.0)";

            // Area Gradient
            if (points.length > 1) {
                const gradient = ctx.createLinearGradient(0, paddingTop, 0, paddingTop + plotH);
                gradient.addColorStop(0, fillColorTop);
                gradient.addColorStop(1, fillColorBottom);

                ctx.beginPath();
                ctx.moveTo(points[0].x, paddingTop + plotH);
                ctx.lineTo(points[0].x, points[0].y);

                for (let i = 0; i < points.length - 1; i++) {
                    const p0 = points[i];
                    const p1 = points[i + 1];
                    const cx = (p0.x + p1.x) / 2;
                    ctx.bezierCurveTo(cx, p0.y, cx, p1.y, p1.x, p1.y);
                }

                ctx.lineTo(points[points.length - 1].x, paddingTop + plotH);
                ctx.closePath();
                ctx.fillStyle = gradient;
                ctx.fill();

                // Draw Stroke
                ctx.beginPath();
                ctx.moveTo(points[0].x, points[0].y);
                for (let i = 0; i < points.length - 1; i++) {
                    const p0 = points[i];
                    const p1 = points[i + 1];
                    const cx = (p0.x + p1.x) / 2;
                    ctx.bezierCurveTo(cx, p0.y, cx, p1.y, p1.x, p1.y);
                }
                ctx.strokeStyle = strokeColor;
                ctx.lineWidth = 2.5;
                ctx.stroke();
            } else if (points.length === 1) {
                // Single point baseline
                ctx.beginPath();
                ctx.moveTo(paddingLeft, points[0].y);
                ctx.lineTo(w - paddingRight, points[0].y);
                ctx.strokeStyle = strokeColor;
                ctx.lineWidth = 2.5;
                ctx.stroke();
            }

            // Draw Data Points
            points.forEach((pt, i) => {
                ctx.beginPath();
                ctx.arc(pt.x, pt.y, 4, 0, Math.PI * 2);
                ctx.fillStyle = "#ffffff";
                ctx.fill();
                ctx.lineWidth = 2;
                ctx.strokeStyle = strokeColor;
                ctx.stroke();
            });

            // Hovered crosshair & active point
            if (hoveredPoint) {
                // Vertical crosshair
                ctx.beginPath();
                ctx.setLineDash([2, 2]);
                ctx.strokeStyle = "#64748b";
                ctx.lineWidth = 1;
                ctx.moveTo(hoveredPoint.x, paddingTop);
                ctx.lineTo(hoveredPoint.x, paddingTop + plotH);
                ctx.stroke();
                ctx.setLineDash([]);

                // Active point outer halo
                ctx.beginPath();
                ctx.arc(hoveredPoint.x, hoveredPoint.y, 7, 0, Math.PI * 2);
                ctx.fillStyle = strokeColor;
                ctx.fill();
                ctx.beginPath();
                ctx.arc(hoveredPoint.x, hoveredPoint.y, 4, 0, Math.PI * 2);
                ctx.fillStyle = "#ffffff";
                ctx.fill();
            }
        }

        // Attach Mouse Move Event for Tooltip
        canvas.addEventListener("mousemove", function (e) {
            const rect = canvas.getBoundingClientRect();
            const mouseX = e.clientX - rect.left;
            const mouseY = e.clientY - rect.top;

            const paddingLeft = 55;
            const paddingRight = 35;
            const plotW = rect.width - paddingLeft - paddingRight;

            if (data.length === 0) return;

            // Find closest point by X coordinate
            let closest = null;
            let minDist = Infinity;

            data.forEach((d, i) => {
                const ptX = data.length > 1
                    ? paddingLeft + (i / (data.length - 1)) * plotW
                    : paddingLeft + plotW / 2;
                const dist = Math.abs(mouseX - ptX);
                if (dist < minDist) {
                    minDist = dist;
                    closest = { ...d, x: ptX, index: i };
                }
            });

            if (closest && minDist < 35) {
                hoveredPoint = closest;
                render();

                if (tooltip) {
                    tooltip.innerHTML = `
                        <div class="gmp-tt-date">${closest.fullDate || closest.date}</div>
                        <div class="gmp-tt-val">GMP: <strong>₹${Number(closest.gmp).toFixed(2)}</strong></div>
                        <div class="gmp-tt-sub">Est. Listing: <strong>₹${Number(closest.estimatedPrice).toFixed(2)}</strong></div>
                        <div class="gmp-tt-gain text-success">+${Number(closest.gainPct).toFixed(2)}% Est. Gain</div>
                        <div class="gmp-tt-tag">Unofficial Quote</div>
                    `;
                    tooltip.classList.remove("hidden");

                    let ttLeft = closest.x - tooltip.offsetWidth / 2;
                    let ttTop = mouseY - tooltip.offsetHeight - 15;

                    if (ttLeft < 5) ttLeft = 5;
                    if (ttLeft + tooltip.offsetWidth > rect.width - 5) {
                        ttLeft = rect.width - tooltip.offsetWidth - 5;
                    }
                    if (ttTop < 5) ttTop = mouseY + 15;

                    tooltip.style.left = ttLeft + "px";
                    tooltip.style.top = ttTop + "px";
                }
            } else {
                if (hoveredPoint !== null) {
                    hoveredPoint = null;
                    render();
                }
                if (tooltip) tooltip.classList.add("hidden");
            }
        });

        canvas.addEventListener("mouseleave", function () {
            hoveredPoint = null;
            render();
            if (tooltip) tooltip.classList.add("hidden");
        });

        window.addEventListener("resize", render);
        render();
    };
})();
