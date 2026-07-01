const fs = require("fs");
const path = require("path");

const RECIPE_DIR = path.join(__dirname, "..", "..", "recipes");

// Per-recipe title overrides + hand-written subtitles, keyed by slug.
const META = {
  VeggieSoup: { subtitle: "The whole garden, talked into one pot." },
  "bang-bang-ramen-bowl": { subtitle: "Egg-roll-in-a-bowl, now with noodles and swagger." },
  encheritos: { subtitle: "Enchilada meets burrito; nobody loses." },
  "four-pepper-chilli": { subtitle: "A four-color pepper rainbow that bites back." },
  "hamburger-helper": { subtitle: "The boxed classic, minus the box." },
  "kale-sausage-soup": { subtitle: "Sausage talks the kale into having fun." },
  "mac-n-squeezies": { subtitle: "Cheese sauce thick enough to squeeze." },
  meatloaf: { subtitle: "Comfort food in a ketchup tie." },
  porkloinrub: { title: "Pork Loin Rub", subtitle: "A dry rub with big roasted-onion dreams." },
  "potato-soup": { subtitle: "A bowl of carbs that hugs back." },
  "ranch-chicken": { subtitle: "The ranch packet does the heavy lifting." },
  thesoup: { title: "Creamy Tomato Tortellini Soup", subtitle: "Tortellini took a swim and stayed for dinner." },
  "thick-cookies": { title: "Thick Cookies", subtitle: "Bakery-thick, no bakery required." },
  "turkey-meatballs": { subtitle: "Five things, twenty minutes, done." },
  "turkey-pot-pie": { title: "Sylvie's Turkey Pot Pie", subtitle: "Leftover turkey's big glow-up." },
  "we-have-to-use-these-lentils-soup": { subtitle: "Born from a pantry ultimatum." },
};

const SMALL_WORDS = new Set(["a", "an", "the", "and", "but", "or", "nor", "for", "to", "of", "in", "on", "with", "n"]);

// Slug = filename without any extension (.md, .txt, or none).
const slugOf = (file) => path.parse(file).name;

function smartTitleCase(str) {
  const words = str.toLowerCase().split(/\s+/);
  return words
    .map((w, i) =>
      i !== 0 && SMALL_WORDS.has(w) ? w : w.charAt(0).toUpperCase() + w.slice(1)
    )
    .join(" ");
}

// "bang-bang-ramen-bowl" / "VeggieSoup" -> "Bang Bang Ramen Bowl"
function titleFromFilename(file) {
  const spaced = slugOf(file)
    .replace(/[-_]+/g, " ")
    .replace(/([a-z])([A-Z])/g, "$1 $2") // split camelCase
    .replace(/\s+/g, " ")
    .trim();
  return smartTitleCase(spaced);
}

// Override > markdown header (trimmed to a sane length) > filename.
function deriveTitle(raw, file) {
  const slug = slugOf(file);
  if (META[slug] && META[slug].title) return META[slug].title;

  const first = (raw.split("\n")[0] || "").trim();
  if (/^#{1,6}\s+/.test(first)) {
    let h = first.replace(/^#{1,6}\s+/, "").trim();
    // Long descriptive headers: keep only the first clause.
    if (h.length > 40) h = h.split(/\s+with\s+|,| and /i)[0].trim();
    return smartTitleCase(h);
  }
  return titleFromFilename(file);
}

// Strip a leading markdown-header title from the body so it isn't shown twice.
function bodyWithoutTitle(raw) {
  const lines = raw.split("\n");
  if (/^#{1,6}\s+/.test((lines[0] || "").trim())) {
    return lines.slice(1).join("\n").trim();
  }
  return raw.trim();
}

// A rough "what's in it" preview for the gallery cards.
function preview(body) {
  return body
    .split("\n")
    .map((l) => l.trim())
    .filter(Boolean)
    .slice(0, 4)
    .join(" · ")
    .slice(0, 120);
}

const SECTION_HEAD = /^[\s>#*-]*\b(ingredients?|instructions?|directions?|method|steps?|preparation)\b[\s:]*$/i;
const STEP_VERB = /^(directions?|preheat|heat|cook|add|mix|place|stir|combine|bake|remove|cover|pour|season|drizzle|serve|saut[eé]|whisk|boil|simmer|bring|melt|spread|fold|transfer|reduce|return|ladle|microwave|on (the )?stove)\b/i;
// A line that opens with a quantity (digit or unicode fraction) reads as an ingredient.
const QTY = /^[*\-•\s]*[\d¼½¾⅓⅔⅛⅜⅝⅞]/;
// Trailing meta sections that must not be treated as method steps.
const STEP_END = /^\s*(notes?|adapted from|storage|nutrition|source|makes|yields?)\b/i;

// In the header-less case, the method starts at the first line that opens with
// a cooking verb. Length is NOT used — ingredient lines can be long too.
function isStepLike(line) {
  const l = line.trim().replace(/^[*\-•]\s*/, "");
  return l ? STEP_VERB.test(l) : false;
}

function cleanItem(line) {
  return line.trim().replace(/^[*\-•]\s*/, "").replace(/^directions?\s*[:.]\s*/i, "").trim();
}

// Turn the method block into discrete steps, handling numbered lists, bullet
// lists, blank-line paragraphs, and one-line-per-step formats. Trailing
// NOTES / "Adapted from" style sections are dropped.
function splitSteps(stepText) {
  const kept = [];
  for (const line of stepText.split("\n")) {
    if (STEP_END.test(line)) break;
    kept.push(line);
  }
  const text = kept.join("\n").trim();
  if (!text) return [];

  // Numbered list: "1. ..." / "2) ..."
  if (/^\s*\d+[.)]\s+/m.test(text)) {
    return text
      .split(/\n(?=\s*\d+[.)]\s+)/)
      .map((s) => cleanItem(s.replace(/^\s*\d+[.)]\s*/, "").replace(/\s*\n\s*/g, " ")))
      .filter(Boolean);
  }
  // Bullet list
  if (/^\s*[*\-•]\s+/m.test(text)) {
    return text
      .split("\n")
      .filter((l) => /^\s*[*\-•]\s+/.test(l))
      .map(cleanItem)
      .filter(Boolean);
  }
  // Blank-line separated paragraphs
  if (/\n\s*\n/.test(text)) {
    return text
      .split(/\n\s*\n/)
      .map((p) => cleanItem(p.replace(/\s*\n\s*/g, " ")))
      .filter(Boolean);
  }
  // One step per line, when each line clearly starts its own instruction.
  const rows = text.split("\n").map((s) => s.trim()).filter(Boolean);
  if (rows.length > 1 && rows.every((l) => /^[A-Z0-9]/.test(l))) {
    return rows.map(cleanItem).filter(Boolean);
  }
  // Otherwise a single wrapped paragraph.
  return [cleanItem(text.replace(/\s*\n\s*/g, " "))].filter(Boolean);
}

// Split a body into { ingredients[], steps[] }.
function parseSections(body) {
  const lines = body.split("\n");

  // 1) Explicit headers anywhere in the file.
  let ingIdx = -1, stepIdx = -1;
  lines.forEach((line, i) => {
    if (!SECTION_HEAD.test(line)) return;
    if (/instr|direct|method|steps?|prepar/i.test(line) && stepIdx === -1) stepIdx = i;
    else if (/ingredient/i.test(line) && ingIdx === -1) ingIdx = i;
  });

  let ingLines, stepLines;
  if (ingIdx !== -1 || stepIdx !== -1) {
    const ingStart = ingIdx === -1 ? 0 : ingIdx + 1;
    const ingEnd = stepIdx > ingStart ? stepIdx : lines.length;
    ingLines = lines.slice(ingStart, stepIdx === -1 ? lines.length : ingEnd);
    stepLines = stepIdx === -1 ? [] : lines.slice(stepIdx + 1);
  } else {
    // 2) Heuristic. Drop a leading title / intro line: the first non-empty line
    // is a title or blurb (not an ingredient) when it isn't a quantity or a
    // cooking step and a blank line follows it.
    let start = 0;
    const firstIdx = lines.findIndex((l) => l.trim());
    if (firstIdx !== -1) {
      const l = lines[firstIdx].trim();
      const nextBlank = (lines[firstIdx + 1] || "").trim() === "";
      if (nextBlank && !QTY.test(l) && !isStepLike(l)) start = firstIdx + 1;
    }
    // Leading list = ingredients, first step-like line starts the method.
    let cut = lines.length;
    for (let i = start; i < lines.length; i++) {
      if (lines[i].trim() && isStepLike(lines[i])) { cut = i; break; }
    }
    ingLines = lines.slice(start, cut);
    stepLines = lines.slice(cut);
  }

  const ingredients = ingLines
    .map(cleanItem)
    .filter(Boolean)
    .filter((l) => !/^(scale|\d+x|description|notes?)$/i.test(l));

  const steps = splitSteps(stepLines.join("\n").trim());

  return { ingredients, steps };
}

module.exports = function () {
  if (!fs.existsSync(RECIPE_DIR)) return [];

  return fs
    .readdirSync(RECIPE_DIR)
    .filter((f) => !f.startsWith("."))
    .filter((f) => fs.statSync(path.join(RECIPE_DIR, f)).isFile())
    .map((file) => {
      const raw = fs.readFileSync(path.join(RECIPE_DIR, file), "utf8");
      const body = bodyWithoutTitle(raw);
      const { ingredients, steps } = parseSections(body);
      const slug = slugOf(file);
      return {
        slug,
        title: deriveTitle(raw, file),
        subtitle: (META[slug] && META[slug].subtitle) || "Sunny, simple, made at home.",
        body,
        ingredients,
        steps,
        preview: preview(body),
      };
    })
    .sort((a, b) => a.title.localeCompare(b.title));
};
