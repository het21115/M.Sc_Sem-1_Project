// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

document.addEventListener("click", function (event) {
	const lotButton = event.target.closest("[data-lot-adjust]");
	if (lotButton) {
		const form = lotButton.closest(".investor-bid-form");
		const lotsInput = form?.querySelector("[name='appliedLots']");
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
	const normalize = function (value) {
		return (value || "").replace(/\s+/g, " ").trim().toLowerCase();
	};
	document.querySelectorAll(".investor-ipo-record").forEach(function (record) {
		if (record.dataset.applyAttached === "true") {
			return;
		}

		const ipoName = normalize(record.querySelector("summary strong")?.textContent);
		const isOpen = /\bopen\b/i.test(record.querySelector("summary small")?.textContent || "");
		const template = templates.find(function (item) {
			return normalize(item.dataset.ipoName) === ipoName;
		}) || (isOpen ? templates.find(function (item) { return !item.dataset.used; }) : null);
		const detailsBody = record.querySelector(".investor-ipo-record-body");
		if (template && detailsBody) {
			detailsBody.appendChild(template.content.cloneNode(true));
			updateInvestorBidForm(detailsBody.querySelector(".investor-bid-form"));
			template.dataset.used = "true";
			record.dataset.applyAttached = "true";
		}
	});
}

if (document.readyState === "loading") {
	document.addEventListener("DOMContentLoaded", attachInvestorApplyButtons);
} else {
	attachInvestorApplyButtons();
}

document.addEventListener("toggle", function (event) {
	if (event.target.matches(".investor-ipo-record")) {
		attachInvestorApplyButtons();
	}
}, true);

function updateInvestorBidForm(form) {
	if (!form) {
		return;
	}
	const lots = Math.max(1, Number(form.querySelector("[name='appliedLots']")?.value || 1));
	const lotSize = Number(form.dataset.lotSize || 0);
	const bidPrice = Number(form.querySelector("[name='bidPrice']")?.value || 0);
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
