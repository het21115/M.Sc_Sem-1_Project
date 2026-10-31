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

	const statusCard = event.target.closest("[data-ipo-status]");
	if (statusCard) {
		const selectedStatus = statusCard.dataset.ipoStatus;
		document.querySelectorAll("#search-ipo-modal [data-market-status]").forEach(function (row) {
			row.hidden = row.dataset.marketStatus !== selectedStatus;
		});
	} else if (event.target.closest("[data-bs-target='#search-ipo-modal']")) {
		document.querySelectorAll("#search-ipo-modal [data-market-status]").forEach(function (row) {
			row.hidden = false;
		});
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
		openFilteredMarketModal();
	});
} else {
	attachInvestorApplyButtons();
	attachInvestorWatchlistButtons();
	openFilteredMarketModal();
}

function openFilteredMarketModal() {
	const query = new URLSearchParams(window.location.search);
	const hasMarketFilter = ["marketIpoName", "marketStatus", "marketIpoType", "marketCompany"]
		.some(function (key) { return query.get(key); });
	const modal = document.getElementById("search-ipo-modal");
	if (hasMarketFilter && modal && window.bootstrap) {
		window.bootstrap.Modal.getOrCreateInstance(modal).show();
	}
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
