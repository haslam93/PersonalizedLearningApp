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