window.upskillTracker = window.upskillTracker || {};

window.upskillTracker.openAnnouncement = async function (announcement) {
    if (!announcement || !announcement.url) {
        throw new Error("An announcement URL is required.");
    }

    const url = new URL(announcement.url);
    if (url.protocol !== "https:" && url.protocol !== "http:") {
        throw new Error("Announcement links must use HTTP or HTTPS.");
    }

    window.open(url.href, "_blank", "noopener,noreferrer");
    const token = document.querySelector("#portal-lock-form input[name='__RequestVerificationToken']")?.value;
    if (!token) {
        console.warn("The announcement opened, but tracking needs a fresh portal session.");
        return false;
    }

    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 10000);
    try {
        const response = await fetch("/api/announcements/opened", {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "X-CSRF-TOKEN": token
            },
            body: JSON.stringify(announcement),
            credentials: "same-origin",
            signal: controller.signal
        });
        if (!response.ok) {
            console.warn(`Announcement tracking failed with HTTP ${response.status}.`);
        }
        return response.ok;
    } catch (error) {
        if (!(error instanceof TypeError) && error.name !== "AbortError") throw error;
        console.warn("The announcement opened, but its read could not be recorded.");
        return false;
    } finally {
        window.clearTimeout(timeout);
    }
};

window.scrollToElement = function (elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
        element.scrollIntoView({ behavior: reducedMotion ? "auto" : "smooth", block: "start" });
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