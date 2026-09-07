(() => {
    const param = new URLSearchParams(window.location.search).get("clawpilotTheme");
    const theme =
        param || (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
    document.documentElement.setAttribute("data-theme", theme);
})();

(() => {
    const key = "learning-portal.theme";
    const queryTheme = new URLSearchParams(window.location.search).get("clawpilotTheme");
    const systemTheme = window.matchMedia("(prefers-color-scheme: dark)");
    let savedTheme;
    try {
        savedTheme = localStorage.getItem(key);
    } catch (error) {
        if (!(error instanceof DOMException)) throw error;
    }
    const isTheme = value => value === "light" || value === "dark";
    const apply = theme => document.documentElement.setAttribute("data-theme", theme);
    apply(isTheme(queryTheme) ? queryTheme : isTheme(savedTheme) ? savedTheme : systemTheme.matches ? "dark" : "light");

    window.upskillTracker = window.upskillTracker || {};
    window.upskillTracker.toggleTheme = () => {
        savedTheme = document.documentElement.getAttribute("data-theme") === "dark" ? "light" : "dark";
        apply(savedTheme);
        try {
            localStorage.setItem(key, savedTheme);
        } catch (error) {
            if (!(error instanceof DOMException)) throw error;
        }
    };
    systemTheme.addEventListener("change", event => {
        if (!isTheme(savedTheme) && !isTheme(queryTheme)) apply(event.matches ? "dark" : "light");
    });
})();
