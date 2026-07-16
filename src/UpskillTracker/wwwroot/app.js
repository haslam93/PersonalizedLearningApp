window.upskillTracker = window.upskillTracker || {};

window.upskillTracker.openAnnouncement = function (announcement) {
    if (!announcement || !announcement.url) {
        return;
    }

    const payload = JSON.stringify(announcement);

    try {
        if (navigator.sendBeacon) {
            const request = new Blob([payload], { type: "application/json" });
            navigator.sendBeacon("/api/announcements/opened", request);
        } else {
            fetch("/api/announcements/opened", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json"
                },
                body: payload,
                credentials: "same-origin",
                keepalive: true
            }).catch(() => {});
        }
    } catch {
        // Keep link opening even if the tracking request fails.
    }

    window.open(announcement.url, "_blank", "noopener,noreferrer");
};

window.scrollToElement = function (elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        element.scrollIntoView({ behavior: "smooth", block: "start" });
    }
};

window.upskillTracker.focusElement = function (elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        element.focus({ preventScroll: true });
        element.scrollIntoView({ block: "nearest", inline: "nearest" });
    }
};

window.upskillTracker.getTimeZoneId = function () {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
};

window.upskillTracker.scrollElementToEnd = function (elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        element.scrollLeft = element.scrollWidth;
    }
};

window.upskillTracker.initializeReconnectRecovery = function () {
    const modal = document.getElementById("components-reconnect-modal");
    const reloadButton = document.getElementById("components-reconnect-reload");
    if (!modal || !reloadButton || modal.dataset.initialized === "true") {
        return;
    }

    modal.dataset.initialized = "true";
    let reloadScheduled = false;
    reloadButton.addEventListener("click", () => window.location.reload());
    modal.addEventListener("keydown", event => {
        if (event.key === "Tab") {
            event.preventDefault();
            reloadButton.focus();
        }
    });

    const handleReconnectState = () => {
        const isVisible = modal.classList.contains("components-reconnect-show") ||
            modal.classList.contains("components-reconnect-failed") ||
            modal.classList.contains("components-reconnect-rejected");

        if (isVisible) {
            window.requestAnimationFrame(() => reloadButton.focus());
        }

        if (modal.classList.contains("components-reconnect-rejected") && !reloadScheduled) {
            reloadScheduled = true;
            window.setTimeout(() => window.location.reload(), 1200);
        }
    };

    const observer = new MutationObserver(handleReconnectState);
    observer.observe(modal, { attributes: true, attributeFilter: ["class"] });
    handleReconnectState();
};