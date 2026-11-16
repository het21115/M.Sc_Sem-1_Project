// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

document.addEventListener("click", function (event) {
	const lotButton = event.target.closest("[data-lot-adjust]");
	if (lotButton) {
		const form = lotButton.closest(".investor-bid-form");
		const lotsInput = form?.querySelector("[name='AppliedLots']");
		if (lotsInput) {
			lotsInput.value = Math.max(1, Number(lotsInput.value || 1) + Number(lotButton.dataset.lotAdjust));
			updateInvestorBidForm(form);
		}
	}


	const marketNavigation = event.target.closest("[data-market-navigation]");
	if (marketNavigation) {
		event.preventDefault();
		const target = document.getElementById(marketNavigation.dataset.marketNavigation);
		const modal = marketNavigation.closest(".modal");
		const navigateToMarket = function () {
			if (target) {
				window.location.hash = marketNavigation.getAttribute("href").substring(1);
				target.scrollIntoView({ behavior: "smooth", block: "start" });
			}
		};

		if (modal && window.bootstrap) {
			modal.addEventListener("hidden.bs.modal", navigateToMarket, { once: true });
			window.bootstrap.Modal.getOrCreateInstance(modal).hide();
		} else {
			navigateToMarket();
		}
		return;
	}

	const backButton = event.target.closest("[data-admin-back]");
	if (!backButton) {
		return;
	}

	const referrer = document.referrer;
	const hasSameSiteReferrer = referrer && new URL(referrer, window.location.href).origin === window.location.origin;
	if (hasSameSiteReferrer && window.history.length > 1) {
		window.history.back();
		return;
	}

	window.location.assign(backButton.dataset.adminBack);
});

function attachInvestorApplyButtons() {
	const templates = Array.from(document.querySelectorAll(".investor-apply-template"));
	document.querySelectorAll(".investor-ipo-record").forEach(function (record) {
		if (record.dataset.applyAttached === "true") {
			return;
		}

		const template = templates.find(function (item) {
			return item.dataset.ipoId === record.dataset.ipoId;
		});
		const detailsBody = record.querySelector(".investor-ipo-record-body");
		if (template && detailsBody) {
			detailsBody.appendChild(template.content.cloneNode(true));
			updateInvestorBidForm(detailsBody.querySelector(".investor-bid-form"));
			template.dataset.used = "true";
			record.dataset.applyAttached = "true";
		}
	});
}

function attachInvestorWatchlistButtons() {
	const templates = Array.from(document.querySelectorAll(".investor-watchlist-template"));
	document.querySelectorAll(".investor-ipo-record").forEach(function (record) {
		if (record.querySelector(".investor-ipo-watchlist-form")) {
			return;
		}

		const template = templates.find(function (item) {
			return item.dataset.ipoId === record.dataset.ipoId;
		});
		const detailsBody = record.querySelector(".investor-ipo-record-body");
		if (template && detailsBody) {
			detailsBody.appendChild(template.content.cloneNode(true));
		}
	});
}

if (document.readyState === "loading") {
	document.addEventListener("DOMContentLoaded", function () {
		attachInvestorApplyButtons();
		attachInvestorWatchlistButtons();
		initModalMarketSearch();
	});
} else {
	attachInvestorApplyButtons();
	attachInvestorWatchlistButtons();
	initModalMarketSearch();
}

function initModalMarketSearch() {
	const modal = document.getElementById("search-ipo-modal");
	if (!modal) return;

	const nameInput = document.getElementById("modal-market-name");
	const statusSelect = document.getElementById("modal-market-status");
	const typeSelect = document.getElementById("modal-market-type");
	const companySelect = document.getElementById("modal-market-company");
	const resetBtn = document.getElementById("modal-market-reset");
	const countBadge = document.getElementById("modal-search-count-badge");
	const emptyMsg = document.getElementById("modal-market-empty");
	const table = document.getElementById("modal-market-table");

	function filterRows() {
		const nameFilter = (nameInput?.value || "").trim().toLowerCase();
		const statusFilter = (statusSelect?.value || "").trim().toLowerCase();
		const typeFilter = (typeSelect?.value || "").trim().toLowerCase();
		const companyFilter = (companySelect?.value || "").trim().toLowerCase();

		let visibleCount = 0;
		const rows = modal.querySelectorAll(".modal-ipo-row");
		rows.forEach(function (row) {
			const rowName = row.dataset.name || "";
			const rowCompany = row.dataset.company || "";
			const rowStatus = (row.dataset.status || "").toLowerCase();
			const rowType = (row.dataset.type || "").toLowerCase();

			const matchesName = !nameFilter || rowName.includes(nameFilter) || rowCompany.includes(nameFilter);
			const matchesStatus = !statusFilter || rowStatus === statusFilter;
			const matchesType = !typeFilter || rowType === typeFilter;
			const matchesCompany = !companyFilter || rowCompany === companyFilter;

			const match = matchesName && matchesStatus && matchesType && matchesCompany;
			row.style.display = match ? "" : "none";
			if (match) visibleCount++;
		});

		if (countBadge) {
			countBadge.textContent = "Showing " + visibleCount + " opportunit" + (visibleCount === 1 ? "y" : "ies");
		}
		if (emptyMsg && table) {
			emptyMsg.style.display = visibleCount === 0 ? "block" : "none";
			table.style.display = visibleCount === 0 ? "none" : "";
		}
	}

	nameInput?.addEventListener("input", filterRows);
	statusSelect?.addEventListener("change", filterRows);
	typeSelect?.addEventListener("change", filterRows);
	companySelect?.addEventListener("change", filterRows);

	resetBtn?.addEventListener("click", function () {
		if (nameInput) nameInput.value = "";
		if (statusSelect) statusSelect.value = "";
		if (typeSelect) typeSelect.value = "";
		if (companySelect) companySelect.value = "";
		filterRows();
	});

	document.addEventListener("click", function (event) {
		const statusCard = event.target.closest("[data-ipo-status]");
		if (statusCard) {
			const targetStatus = statusCard.dataset.ipoStatus || "";
			if (statusSelect) {
				statusSelect.value = targetStatus;
			}
			if (nameInput) nameInput.value = "";
			if (typeSelect) typeSelect.value = "";
			if (companySelect) companySelect.value = "";
			filterRows();
		}
	});
}

document.addEventListener("toggle", function (event) {
	if (event.target.matches(".investor-ipo-record")) {
		attachInvestorApplyButtons();
		attachInvestorWatchlistButtons();
	}
}, true);

document.addEventListener("click", function (event) {
	const watchlistLink = event.target.closest(".watchlist-ipo-link");
	if (!watchlistLink) {
		return;
	}

	const record = document.querySelector(watchlistLink.getAttribute("href"));
	if (record && record.matches(".investor-ipo-record")) {
		record.open = true;
		record.scrollIntoView({ behavior: "smooth", block: "center" });
	}
});

function updateInvestorBidForm(form) {
	if (!form) {
		return;
	}
	const lots = Math.max(1, Number(form.querySelector("[name='AppliedLots']")?.value || 1));
	const lotSize = Number(form.dataset.lotSize || 0);
	const bidPrice = Number(form.querySelector("[name='BidPrice']")?.value || 0);
	const shares = lots * lotSize;
	const total = shares * bidPrice;
	const sharesOutput = form.querySelector("[data-bid-shares]");
	const totalOutput = form.querySelector("[data-bid-total]");
	if (sharesOutput) sharesOutput.textContent = shares.toLocaleString("en-IN");
	if (totalOutput) totalOutput.textContent = `₹${total.toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

document.addEventListener("input", function (event) {
	const bidForm = event.target.closest(".investor-bid-form");
	if (bidForm) updateInvestorBidForm(bidForm);

});
