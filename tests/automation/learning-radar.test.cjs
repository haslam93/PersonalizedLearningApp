const assert = require("node:assert/strict");
const { test } = require("node:test");
const fs = require("node:fs");
const path = require("node:path");

const directory = path.resolve(__dirname, "..", "..", "src", "UpskillTracker", "Data", "SeedData");
const schema = JSON.parse(fs.readFileSync(path.join(directory, "learning-radar.schema.json"), "utf8"));
const document = JSON.parse(fs.readFileSync(path.join(directory, "learning-radar.json"), "utf8"));

function validate(value, rule, location) {
    const type = Array.isArray(value) ? "array" : typeof value;
    assert.equal(rule.type === "integer" ? Number.isInteger(value) : type === rule.type, true, `${location}: invalid ${rule.type}`);
    if (rule.required) {
        for (const key of rule.required) assert.ok(Object.hasOwn(value, key), `${location}: missing ${key}`);
    }
    if (rule.properties) {
        for (const [key, child] of Object.entries(rule.properties)) {
            if (Object.hasOwn(value, key)) validate(value[key], child, `${location}.${key}`);
        }
    }
    if (rule.items) value.forEach((item, index) => validate(item, rule.items, `${location}[${index}]`));
    if (rule.enum) assert.ok(rule.enum.includes(value), `${location}: invalid enum`);
    if (rule.maxLength !== undefined) assert.ok(value.length <= rule.maxLength, `${location}: too long`);
    if (rule.minimum !== undefined) assert.ok(value >= rule.minimum, `${location}: below minimum`);
    if (rule.maximum !== undefined) assert.ok(value <= rule.maximum, `${location}: above maximum`);
    if (rule.format === "date") {
        assert.match(value, /^\d{4}-\d{2}-\d{2}$/, `${location}: use yyyy-MM-dd`);
        const date = new Date(`${value}T00:00:00Z`);
        assert.ok(Number.isFinite(date.valueOf()), `${location}: invalid date`);
        assert.equal(date.toISOString().slice(0, 10), value, `${location}: invalid calendar date`);
    }
    if (rule.format === "uri") {
        assert.equal(new URL(value).protocol, "https:", `${location}: use an HTTPS source`);
    }
}

test("the complete curated learning radar satisfies its declared schema", () => {
    validate(document, schema, "radar");
    const titles = document.items.map(item => item.title.trim().toLowerCase());
    assert.equal(new Set(titles).size, titles.length, "Radar titles must be unique.");
    for (const item of document.items) {
        assert.ok(item.title.trim() && item.domain.trim() && item.description.trim(), "Required learning text must not be blank.");
    }
});
