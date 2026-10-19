import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const bundlePath = resolve(
  root,
  "CodexVsix/UI/CodexWebview/webview/assets/composer-B3BCMq_W.js",
);
let source = readFileSync(bundlePath, "utf8");
const original = "We=b[\u0060full-access\u0060]?(0,Q.jsx)(So.Item,{LeftIcon:pl";
const marker = "style:{color:\"#d4a72c\"},LeftIcon:pl";
const descriptionOriginal = "allowWrap:!0,SubText:(0,Q.jsx)(\u0060span\u0060,{className:\u0060text-token-description-foreground\u0060,children:K?";
const descriptionMarker = "allowWrap:!0,SubText:(0,Q.jsx)(\u0060span\u0060,{className:\u0060text-token-description-foreground\u0060,style:{color:\"#d4a72c\"},children:K?";

if (!source.includes(marker)) {
  const matches = source.split(original).length - 1;
  if (matches !== 1) {
    throw new Error(`Expected one Full Access menu item to patch; found ${matches}.`);
  }
  source = source.replace(original, `We=b[\u0060full-access\u0060]?(0,Q.jsx)(So.Item,{${marker}`);
}

if (!source.includes(descriptionMarker)) {
  const matches = source.split(descriptionOriginal).length - 1;
  if (matches !== 1) {
    throw new Error(`Expected one Full Access description to patch; found ${matches}.`);
  }
  source = source.replace(descriptionOriginal, descriptionMarker);
}

writeFileSync(bundlePath, source, "utf8");
process.stdout.write("Applied explicit gold warning color to the Full Access menu row and description.\n");
