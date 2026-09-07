const { test } = require("node:test");
const assert = require("node:assert/strict");
const { spawn } = require("node:child_process");
const { once } = require("node:events");
const { randomInt } = require("node:crypto");
const { mkdtemp, mkdir, rm, writeFile } = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const net = require("node:net");
const { chromium } = require("playwright");

const project = path.resolve(__dirname, "..", "..", "src", "UpskillTracker");
const artifacts = path.join(__dirname, "test-results");

async function freePort() {
    const server = net.createServer();
    server.listen(0, "127.0.0.1");
    await once(server, "listening");
    const port = server.address().port;
    await new Promise(resolve => server.close(resolve));
    return port;
}

async function waitForApp(url, child) {
    for (let attempt = 0; attempt < 90; attempt++) {
        assert.equal(child.exitCode, null, "The isolated test app exited during startup.");
        try {
            const response = await fetch(`${url}/healthz`, { signal: AbortSignal.timeout(2000) });
            if (response.ok) return;
        } catch (error) {
            if (!(error instanceof TypeError) && error.name !== "TimeoutError") throw error;
        }
        await new Promise(resolve => setTimeout(resolve, 500));
    }
    throw new Error("The isolated app did not become ready.");
}

async function noHorizontalOverflow(page) {
    const sizes = await page.evaluate(() => ({
        content: document.documentElement.scrollWidth,
        viewport: document.documentElement.clientWidth
    }));
    assert.ok(sizes.content <= sizes.viewport + 1, `Horizontal overflow: ${JSON.stringify(sizes)}`);
}

async function dashboard(page) {
    await page.getByRole("heading", { name: "Make your next session count." }).waitFor();
    await page.locator(".topic-row").first().waitFor();
}

test("protected, persistent, accessible learning journeys", { timeout: 240000 }, async t => {
    const temp = await mkdtemp(path.join(os.tmpdir(), "learning-portal-browser-"));
    await mkdir(artifacts, { recursive: true });
    const pin = randomInt(100000, 1000000).toString();
    const port = await freePort();
    const url = `http://127.0.0.1:${port}`;
    const child = spawn("dotnet", [path.join(project, "bin", "Release", "net8.0", "UpskillTracker.dll")], {
        cwd: project,
        windowsHide: true,
        env: {
            ...process.env,
            ASPNETCORE_ENVIRONMENT: "Development",
            ASPNETCORE_URLS: url,
            AccessPin: pin,
            Storage__Provider: "Sqlite",
            Storage__ConnectionString: `Data Source=${path.join(temp, "learning.db")}`,
            Storage__EnableLegacySqliteImport: "false",
            Storage__KeyBlobUri: "",
            Storage__UseManagedIdentity: "false",
            GitHubOAuth__ClientId: "",
            GitHubOAuth__ClientSecret: "",
            YouTube__ApiKey: "",
            APPLICATIONINSIGHTS_CONNECTION_STRING: "",
            ApplicationInsights__ConnectionString: ""
        },
        stdio: ["ignore", "pipe", "pipe"]
    });
    let serverLog = "";
    child.stdout.on("data", chunk => { serverLog += chunk; });
    child.stderr.on("data", chunk => { serverLog += chunk; });
    let browser;
    try {
        await waitForApp(url, child);
        browser = await chromium.launch({
            headless: true,
            ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {})
        });
        const context = await browser.newContext({
            viewport: { width: 1440, height: 1100 },
            colorScheme: "light",
            locale: "en-US",
            timezoneId: "America/Toronto"
        });
        const page = await context.newPage();
        page.setDefaultTimeout(20000);
        const browserErrors = [];
        page.on("pageerror", error => browserErrors.push(error.message));
        page.on("console", message => {
            if (message.type() === "error" && /unhandled exception/i.test(message.text())) browserErrors.push(message.text());
        });

        await t.test("a browser flag cannot bypass the PIN", async () => {
            await page.goto(url);
            await page.locator("input[type=password]").waitFor();
            await page.evaluate(() => sessionStorage.setItem("upskilltracker.pin-unlocked", "True"));
            await page.reload();
            await page.locator("input[type=password]").waitFor();
            assert.equal(await page.locator("#studio-title").count(), 0);
        });

        await page.locator("input[type=password]").fill(pin);
        await page.getByRole("button", { name: /Unlock/ }).click();
        await dashboard(page);

        await t.test("desktop layout gives usable learning visuals and session budgets", async () => {
            await noHorizontalOverflow(page);
            await page.getByRole("button", { name: "15 min", exact: true }).click();
            await page.locator('.studio-segmented button[aria-pressed="true"]').filter({ hasText: "15 min" }).waitFor();
            assert.match(await page.locator(".session-path").innerText(), /Learn\s+5 min/);
            assert.match(await page.locator(".session-path").innerText(), /Apply\s+7 min/);
            assert.match(await page.locator(".session-path").innerText(), /Recall\s+3 min/);
            await page.getByRole("button", { name: "30 min", exact: true }).click();
            const row = await page.locator(".topic-row").first().boundingBox();
            const track = await page.locator(".topic-track").first().boundingBox();
            assert.ok(track.width >= row.width * 0.9, "Topic progress should use the available card width.");
            await page.screenshot({ path: path.join(artifacts, "dashboard-desktop.png") });
        });

        await t.test("topic map opens an exact plan filter and browser back works", async () => {
            const topic = (await page.locator(".topic-row-title strong").first().innerText()).trim();
            await page.locator(".topic-row").first().click();
            await page.waitForURL(/view=plan/);
            await page.locator(".planner-item-card").first().waitFor();
            const domains = await page.locator(".planner-item-domain").allTextContents();
            assert.ok(domains.length > 0);
            assert.ok(domains.every(domain => domain.trim().startsWith(`${topic} `)), JSON.stringify(domains));
            await page.goBack();
            await dashboard(page);
        });

        await t.test("recall reveals saved context and persists a scheduled reflection", async () => {
            const recall = page.locator("#recall-practice");
            await recall.getByLabel("Your explanation, from memory", { exact: true }).fill("An agent can plan work and call tools, but needs explicit permission boundaries, reproducible evaluations, and human review before consequential actions.");
            await page.getByRole("tab", { name: "Notes", exact: true }).click();
            await page.getByLabel("Search notes", { exact: true }).waitFor();
            await page.getByRole("tab", { name: "Dashboard", exact: true }).click();
            await dashboard(page);
            assert.match(await recall.getByLabel("Your explanation, from memory", { exact: true }).inputValue(), /explicit permission boundaries/);
            assert.equal(await page.locator("#recall-reference").count(), 0);
            await recall.getByRole("button", { name: "Reveal saved context", exact: true }).click();
            await page.locator("#recall-reference").waitFor();
            await recall.getByRole("button", { name: /I can explain it/ }).click();
            await recall.getByLabel("One thing to try or clarify next (optional)", { exact: true }).fill("Build an agent with read-only tools, then test a denied write and record the result.");
            const label = await recall.locator(".recall-confidence button strong").first().boundingBox();
            const interval = await recall.locator(".recall-confidence button span").first().boundingBox();
            assert.ok(interval.y >= label.y + label.height, "Confidence and review interval must be separate readable lines.");
            await recall.getByText("How did that feel?", { exact: true }).click();
            await recall.locator("button:not([disabled])").filter({ hasText: "Save reflection & schedule review" }).waitFor();
            await recall.screenshot({ path: path.join(artifacts, "active-recall.png") });
            await recall.getByRole("button", { name: "Save reflection & schedule review", exact: true }).click();
            await recall.locator('[role="status"]').filter({ hasText: "Reflection saved to Notes and History" }).waitFor();
            await page.getByRole("tab", { name: "Notes", exact: true }).click();
            await page.waitForURL(/view=notes/);
            await page.getByLabel("Search notes", { exact: true }).fill("explicit permission boundaries");
            await page.locator(".note-card").filter({ hasText: "explicit permission boundaries" }).waitFor();
            await page.reload();
            await page.locator(".note-card").filter({ hasText: "explicit permission boundaries" }).waitFor();
            assert.match(page.url(), /view=notes/);
        });

        await t.test("destructive note actions can be cancelled", async () => {
            const note = page.locator(".note-card").filter({ hasText: "explicit permission boundaries" });
            await note.getByRole("button", { name: "Edit", exact: true }).click();
            await page.getByRole("button", { name: "Delete", exact: true }).click();
            await page.getByRole("button", { name: "Keep note", exact: true }).click();
            await note.waitFor();
        });

        await t.test("announcement tracking sends the real antiforgery token", async () => {
            const result = await page.evaluate(async () => {
                const originalOpen = window.open;
                const opened = [];
                window.open = url => { opened.push(url); return null; };
                try {
                    const tracked = await window.upskillTracker.openAnnouncement({
                        url: "https://example.invalid/browser-announcement",
                        title: "Browser announcement transport proof",
                        summary: "A local integration fixture, not an external request.",
                        source: "Browser tests",
                        topic: "Learning",
                        stream: "MicrosoftOfficial"
                    });
                    return { tracked, opened };
                } finally {
                    window.open = originalOpen;
                }
            });
            assert.equal(result.tracked, true);
            assert.deepEqual(result.opened, ["https://example.invalid/browser-announcement"]);
            const rejected = await context.request.post(`${url}/api/announcements/opened`, {
                data: { url: "https://example.invalid/missing-token", title: "Must not be saved" }
            });
            assert.equal(rejected.status(), 400);
        });

        await t.test("light and dark themes persist and keyboard focus is visible", async () => {
            await page.getByRole("button", { name: "Toggle light and dark theme", exact: true }).click();
            assert.equal(await page.locator("html").getAttribute("data-theme"), "dark");
            await page.reload();
            await page.locator(".note-card").first().waitFor();
            assert.equal(await page.locator("html").getAttribute("data-theme"), "dark");
            await page.goto(url);
            await dashboard(page);
            await page.screenshot({ path: path.join(artifacts, "dashboard-dark.png") });
            await page.getByRole("button", { name: "Toggle light and dark theme", exact: true }).focus();
            await page.keyboard.press("Tab");
            const focus = await page.evaluate(() => getComputedStyle(document.activeElement).outlineStyle);
            assert.notEqual(focus, "none");
        });

        await t.test("all tabs survive at mobile width without page overflow", async () => {
            await page.setViewportSize({ width: 390, height: 844 });
            const views = [
                ["dashboard", ".learning-studio"],
                ["plan", ".planner-item-card"],
                ["certifications", ".certification-intro-card"],
                ["timeline", ".timeline-intro-card"],
                ["history", ".history-intro-card"],
                ["tools", ".tools-intro-card"],
                ["resources", ".resources-intro-card"],
                ["videos", ".video-canvas-header"],
                ["notes", ".notes-intro-card"],
                ["copilot", ".copilot-intro-card"]
            ];
            for (const [view, selector] of views) {
                await page.goto(`${url}/?view=${view}`);
                await page.locator(selector).first().waitFor();
                await noHorizontalOverflow(page);
                assert.equal(await page.locator("#blazor-error-ui").isVisible(), false, `${view} crashed`);
            }
            await page.goto(`${url}/?clawpilotTheme=light`);
            await dashboard(page);
            await page.screenshot({ path: path.join(artifacts, "dashboard-mobile.png"), fullPage: true });
        });

        await t.test("locking the portal rejects subsequent writes and reloads", async () => {
            await page.getByRole("button", { name: "Lock portal", exact: true }).click();
            await page.locator("input[type=password]").waitFor();
            const rejected = await context.request.post(`${url}/api/announcements/opened`, {
                data: { url: "https://example.invalid/locked", title: "Must not be saved" }
            });
            assert.equal(rejected.status(), 401);
            await page.goto(`${url}/?view=notes`);
            await page.locator("input[type=password]").waitFor();
            assert.equal(await page.locator(".notes-library-card").count(), 0);
            await page.goto(`${url}/auth/pin?clawpilotTheme=dark`);
            await page.locator("input[type=password]").waitFor();
            assert.equal(await page.locator("html").getAttribute("data-theme"), "dark");
            assert.equal(await page.locator("input[type=password]").evaluate(input => getComputedStyle(input).backgroundColor), "rgb(52, 50, 49)");
            await page.screenshot({ path: path.join(artifacts, "pin-mobile-dark.png"), fullPage: true });
        });

        assert.deepEqual(browserErrors, [], "No unhandled browser or Blazor exceptions.");
    } finally {
        if (browser) await browser.close();
        if (child.exitCode === null) {
            const exited = once(child, "exit");
            child.kill();
            await exited;
        }
        await writeFile(path.join(artifacts, "app.log"), serverLog.replaceAll(pin, "[test PIN]"));
        await rm(temp, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    }
});
