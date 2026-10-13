const assert = require("node:assert/strict");
const { test } = require("node:test");
const { spawnSync } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

if (process.env.FAKE_AZ_PROCESS === "1") {
    const file = process.env.FAKE_AZ_STATE_FILE;
    const fixture = JSON.parse(fs.readFileSync(file, "utf8"));
    const args = process.argv.slice(2);
    const command = args.slice(0, args[0] === "tag" ? 2 : 3).join(" ");
    fixture.calls.push(args);
    const save = () => fs.writeFileSync(file, JSON.stringify(fixture));
    if (fixture.failCommand === command) {
        save();
        console.error("Simulated Azure CLI failure");
        process.exit(2);
    }
    const query = args[args.indexOf("--query") + 1];
    let output = "";
    if (command === "postgres flexible-server list") {
        output = fixture.servers.join("\n");
    } else if (command === "postgres flexible-server show") {
        if (query === "id") output = "/subscriptions/test/resourceGroups/test/providers/Microsoft.DBforPostgreSQL/flexibleServers/test-pg";
        else if (query === "tags.CostControl") output = fixture.tags.CostControl || "";
        else if (query === "state") {
            output = fixture.states[Math.min(fixture.stateIndex++, fixture.states.length - 1)];
            if (output === "Ready" && fixture.clearTagWhenReady && !fixture.tagCleared) {
                delete fixture.tags.CostControl;
                fixture.tagCleared = true;
            }
        } else throw new Error(`Unexpected query: ${query}`);
    } else if (command === "tag update") {
        if (!fixture.ignoreTagWrite) fixture.tags.CostControl = "Ignore";
        assert.equal(args[args.indexOf("--operation") + 1], "Merge");
    } else if (command === "postgres flexible-server start") {
        assert.ok(args.includes("--no-wait"), "Recovery must use bounded polling, not an unbounded start.");
    } else throw new Error(`Unexpected command: ${command}`);
    assert.equal(args[args.indexOf("--subscription") + 1], "test-subscription");
    save();
    if (output) console.log(output);
} else {
    function run(overrides = {}, mode = "recover", actionEvent = "") {
        const temp = fs.mkdtempSync(path.join(os.tmpdir(), "postgres-guard-test-"));
        const stateFile = path.join(temp, "state.json");
        const fixture = {
            servers: ["test-pg"], states: ["Ready"], stateIndex: 0,
            tags: { owner: "keep-me" }, calls: [], ...overrides
        };
        fs.writeFileSync(stateFile, JSON.stringify(fixture));
        fs.writeFileSync(path.join(temp, "az"), '#!/usr/bin/env bash\nFAKE_AZ_PROCESS=1 node "$FAKE_AZ_DRIVER" "$@"\n', { mode: 0o755 });
        try {
            const result = spawnSync("bash", [path.resolve(__dirname, "..", "..", "scripts", "ensure-postgres-available.sh")], {
                encoding: "utf8", windowsHide: true, timeout: 30000,
                env: {
                    ...process.env,
                    PATH: `${temp}${path.delimiter}${process.env.PATH}`,
                    FAKE_AZ_DRIVER: __filename,
                    FAKE_AZ_STATE_FILE: stateFile,
                    AZURE_SUBSCRIPTION_ID: "test-subscription",
                    AZURE_RESOURCE_GROUP: "test",
                    POSTGRES_GUARD_MODE: mode,
                    GITHUB_ACTIONS: actionEvent ? "true" : "false",
                    GITHUB_EVENT_NAME: actionEvent,
                    SERVER_READY_MAX_ATTEMPTS: "4",
                    SERVER_READY_RETRY_DELAY: "0",
                    GITHUB_STEP_SUMMARY: ""
                }
            });
            assert.ifError(result.error);
            return { ...result, fixture: JSON.parse(fs.readFileSync(stateFile, "utf8")) };
        } finally {
            fs.rmSync(temp, { recursive: true, force: true });
        }
    }

    test("a running server with missing tags is repaired without restarting it", () => {
        const result = run();
        assert.equal(result.status, 0, result.stderr);
        assert.equal(result.fixture.tags.CostControl, "Ignore");
        assert.equal(result.fixture.tags.owner, "keep-me");
        assert.ok(!result.fixture.calls.some(args => args.includes("start")));
        assert.match(result.stdout, /Verified: PostgreSQL is Ready and CostControl=Ignore/);
    });

    test("a protected running server requires no write", () => {
        const result = run({ tags: { CostControl: "Ignore" } });
        assert.equal(result.status, 0, result.stderr);
        assert.ok(!result.fixture.calls.some(args => args.includes("update") || args.includes("start")));
    });

    test("read-only monitoring never repairs a missing tag or restarts a stopped server", () => {
        for (const overrides of [{}, { states: ["Stopped"], tags: { CostControl: "Ignore" } }]) {
            const result = run(overrides, "check");
            assert.notEqual(result.status, 0);
            assert.match(result.stdout, /do not override cost governance/);
            assert.ok(!result.fixture.calls.some(args => args.includes("update") || args.includes("start")));
        }
    });

    test("a protected Ready server passes read-only monitoring and invalid modes fail closed", () => {
        const result = run({ tags: { CostControl: "Ignore" } }, "check");
        assert.equal(result.status, 0, result.stderr);
        assert.match(result.stdout, /Read-only check/);
        assert.ok(!result.fixture.calls.some(args => args.includes("update") || args.includes("start")));
        const invalid = run({}, "renew-forever");
        assert.notEqual(invalid.status, 0);
        assert.equal(invalid.fixture.calls.length, 0);
    });

    test("Actions recovery cannot be triggered by schedules or pushes", () => {
        for (const event of ["schedule", "push"]) {
            const result = run({}, "recover", event);
            assert.notEqual(result.status, 0);
            assert.equal(result.fixture.calls.length, 0);
            assert.match(result.stdout, /explicit manual workflow dispatch/);
        }
        const manual = run({}, "recover", "workflow_dispatch");
        assert.equal(manual.status, 0, manual.stderr);
        assert.equal(manual.fixture.tags.CostControl, "Ignore");
    });
    test("a stopped server is tagged before starting and started only once", () => {
        const result = run({ states: ["Stopped", "Stopped", "Starting", "Ready"] });
        assert.equal(result.status, 0, result.stderr);
        const starts = result.fixture.calls.filter(args => args.includes("start"));
        assert.equal(starts.length, 1);
        const tagIndex = result.fixture.calls.findIndex(args => args[0] === "tag");
        const startIndex = result.fixture.calls.findIndex(args => args.includes("start"));
        assert.ok(tagIndex >= 0 && tagIndex < startIndex);
    });

    test("tag drift during a state transition is repaired again before success", () => {
        const result = run({ states: ["Updating", "Ready"], clearTagWhenReady: true });
        assert.equal(result.status, 0, result.stderr);
        assert.equal(result.fixture.calls.filter(args => args[0] === "tag").length, 2);
        assert.equal(result.fixture.tags.CostControl, "Ignore");
    });

    test("missing or ambiguous discovery fails without modifying a server", () => {
        for (const servers of [[], ["first", "second"]]) {
            const result = run({ servers });
            assert.notEqual(result.status, 0);
            assert.match(result.stdout, /::error::/);
            assert.equal(result.fixture.calls.length, 1);
        }
    });

    test("CLI and ignored tag writes cannot produce a successful guard", () => {
        for (const overrides of [{ failCommand: "postgres flexible-server list" }, { ignoreTagWrite: true }]) {
            const result = run(overrides);
            assert.notEqual(result.status, 0);
            assert.doesNotMatch(result.stdout, /Verified:/);
        }
    });

    test("unrecoverable states and exhausted polling fail explicitly", () => {
        for (const states of [["Disabled"], ["Starting"]]) {
            const result = run({ states });
            assert.notEqual(result.status, 0);
            assert.match(result.stdout, /::error::PostgreSQL/);
            assert.doesNotMatch(result.stdout, /Verified:/);
            assert.ok(result.fixture.stateIndex <= 4);
        }
    });
}
