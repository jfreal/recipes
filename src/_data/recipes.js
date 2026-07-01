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
  const base = file.replace(/\.md$/i, "");
  const spaced = base
    .replace(/[-_]+/g, " ")
    .replace(/([a-z])([A-Z])/g, "$1 $2") // split camelCase
    .replace(/\s+/g, " ")
    .trim();
  return smartTitleCase(spaced);
}

// Override > markdown header (trimmed to a sane length) > filename.
function deriveTitle(raw, file) {
  const slug = file.replace(/\.md$/i, "");
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

// In the header-less case, the method starts at the first line that opens with
// a cooking verb. Length is NOT used — ingredient lines can be long too.
function isStepLike(line) {
  const l = line.trim().replace(/^[*\-•]\s*/, "");
  return l ? STEP_VERB.test(l) : false;
}

function cleanItem(line) {
  return line.trim().replace(/^[*\-•]\s*/, "").replace(/^directions?\s*[:.]\s*/i, "").trim();
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
    // 2) Heuristic: leading list = ingredients, first step-like line starts the method.
    let cut = lines.length;
    for (let i = 0; i < lines.length; i++) {
      if (lines[i].trim() && isStepLike(lines[i])) { cut = i; break; }
    }
    ingLines = lines.slice(0, cut);
    stepLines = lines.slice(cut);
  }

  const ingredients = ingLines
    .map(cleanItem)
    .filter(Boolean)
    .filter((l) => !/^(scale|\d+x|description|notes?)$/i.test(l));

  // Steps: prefer bullet markers, else split on blank lines.
  const stepText = stepLines.join("\n").trim();
  let steps = [];
  if (/^[*\-•]\s+/m.test(stepText)) {
    steps = stepText
      .split("\n")
      .filter((l) => /^[*\-•]\s+/.test(l.trim()))
      .map(cleanItem)
      .filter(Boolean);
  } else if (stepText) {
    steps = stepText
      .split(/\n\s*\n/)
      .map((p) => p.replace(/\s*\n\s*/g, " ").trim())
      .map((p) => cleanItem(p))
      .filter(Boolean);
  }

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
      const slug = file.replace(/\.md$/i, "");
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
